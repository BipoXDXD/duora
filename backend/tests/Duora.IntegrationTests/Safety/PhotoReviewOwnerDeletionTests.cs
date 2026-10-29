using System.Net;
using System.Security.Cryptography;
using Duora.Core.Identity;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Duora.IntegrationTests.Media;
using Duora.Web.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>API-M03 durante a revisão humana: o titular exclui, o caso some para o moderador e o candidato é limpo.</summary>
public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    public async Task TheOwnerDeletesAPhotoUnderReviewAndTheModeratorCanNoLongerReachIt()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        using var ownerBrowser = await identity.SignedInBrowserAsync(owner);
        var moderator = await identity.CreatePrivilegedAccountAsync(AccountRoles.Moderator);
        await ExecuteAsync("update identity.users set status='Active' where id=$1", moderator.UserId);
        using var moderatorBrowser = await identity.SignedInBrowserAsync(moderator);
        await MfaScenario.EnableAsync(moderatorBrowser, _time);
        foreach (var capability in new[] { PhotoReviewCapability.Assign, PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide })
        {
            await GrantAsync(moderator.UserId, capability, true);
        }
        var item = await HttpCaseAsync(owner.UserId);
        var path = $"/api/v1/admin/photo-reviews/{item.CaseId}";
        using (var assignment = await moderatorBrowser.PostJsonAsync(path + "/assignments", new { reviewerId = moderator.UserId },
            ReviewCommand("case", item.CaseId, 1)))
        {
            Assert.Equal(HttpStatusCode.OK, assignment.StatusCode);
        }
        var generation = await CleanupGenerationAsync(item);
        var candidates = Files().Where(file => file.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, candidates.Length);

        using var deleted = await PhotoDeletionTests.DeleteAsync(ownerBrowser, item.UploadId,
            PhotoReviewHeaders.Tag("photo", item.UploadId, item.UploadVersion), Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates c join media.photo_tombstones t on t.photo_id = c.upload_id
            where c.generation = $1 and c.cleanup_requested_at = t.deleted_at and c.deleted_at is null
            """, generation));
        using (var decision = await moderatorBrowser.PostJsonAsync(path + "/decisions",
            new { decision = "approve", reasonCode = "meets_policy" }, ReviewCommand("case", item.CaseId, 2)))
        {
            Assert.Equal(HttpStatusCode.NotFound, decision.StatusCode);
        }
        using (var evidence = await moderatorBrowser.GetAsync(path + "/evidence/full"))
        {
            Assert.Equal(HttpStatusCode.NotFound, evidence.StatusCode);
        }
        using (var queue = await moderatorBrowser.GetAsync("/api/v1/admin/photo-reviews"))
        {
            Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
            Assert.DoesNotContain(item.CaseId.ToString(), await queue.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        }

        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(1, await ApprovedCleanup(source).DispatchOnceAsync(Ct));
        Assert.Empty(Files().Intersect(candidates));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Fact]
    public async Task ApprovalOfADeletedPhotoIsNotFoundAndPublishesNothing()
    {
        var (reviewer, item) = await PreparedAsync();
        await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=$1", item.UploadId);

        Assert.IsType<PhotoReviewApprovalResult.NotFound>(await ApproveAsync(reviewer, item, Key()));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Fact]
    public async Task ADeletionTakesOverTheCleanupMarkerAnExpirationSetBefore()
    {
        await using var web = ReviewWeb();
        var item = await CaseAsync();
        _time.Advance(ReviewWait);
        Assert.Equal(1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        var generation = await CleanupGenerationAsync(item);
        var marker = await MarkerAsync(generation);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.IsType<PhotoDeletionResult.Deleted>(await DeleteAsOwnerAsync(web, item));

        // The earlier marker stays; the expiration collector no longer matches a Deleted photo.
        Assert.Equal(marker, await MarkerAsync(generation));
        await using var source = NpgsqlDataSource.Create(_connectionString);
        Assert.Equal(0, await ExpiredCleanup(source).DispatchOnceAsync(Ct));
        Assert.Equal(1, await ApprovedCleanup(source).DispatchOnceAsync(Ct));
        Assert.DoesNotContain(Files(), file => file.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeletionWaitsForTheLeaseAnotherCollectorAlreadyHolds()
    {
        await using var web = ReviewWeb();
        var item = await CaseAsync();
        _time.Advance(ReviewWait);
        Assert.Equal(1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        await using var source = NpgsqlDataSource.Create(_connectionString);
        var expired = Assert.IsType<ExpiredCandidateCleanupClaim>(await ExpiredCleanup(source).ClaimAsync(Ct));
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.IsType<PhotoDeletionResult.Deleted>(await DeleteAsOwnerAsync(web, item));

        var decided = ApprovedCleanup(source);
        Assert.Null(await decided.ClaimAsync(Ct));
        _time.Advance(DecidedCandidateCleanupDispatcher.LeaseDuration + TimeSpan.FromSeconds(1));
        var takeover = Assert.IsType<DecidedCandidateCleanupClaim>(await decided.ClaimAsync(Ct));
        Assert.Equal(expired.Generation, takeover.Generation);
        Assert.False(await ExpiredCleanup(source).CompleteSweepAsync(expired, Ct));
        Assert.True(await decided.CompleteSweepAsync(takeover, Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.review_candidates where generation=$1 and deleted_at is not null", takeover.Generation));
    }

    [Theory]
    [InlineData("before-copies")]
    [InlineData("before-confirm")]
    public async Task ADeletionDuringAnApprovalNeverLeavesAnApprovedAssetOfADeletedPhoto(string moment)
    {
        await using var web = ReviewWeb();
        var (reviewer, item) = await PreparedAsync();
        var deleted = false;
        async Task DeleteOnceAsync()
        {
            if (!deleted)
            {
                deleted = true;
                Assert.IsType<PhotoDeletionResult.Deleted>(await DeleteAsOwnerAsync(web, item));
            }
        }
        var puts = 0;
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName),
            beforeOpen: moment == "before-copies" ? _ => DeleteOnceAsync() : null,
            afterPut: moment == "before-confirm" ? _ => ++puts == 3 ? DeleteOnceAsync() : Task.CompletedTask : null);

        var result = await new PhotoReviewApprovals(Factory(), store, _time)
            .ApproveAsync(reviewer, item.CaseId, 2, PhotoReviewApprovalReason.MeetsPolicy, Key(), Ct);

        Assert.True(deleted);
        Assert.True(result is PhotoReviewApprovalResult.NotFound or PhotoReviewApprovalResult.Unavailable, result.ToString());
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1 and state='Approved'", item.UploadId));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Deleted'", item.UploadId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
    }

    [Fact]
    public async Task ADeletionDuringARejectionLeavesNoDecision()
    {
        await using var web = ReviewWeb();
        var (reviewer, item) = await PreparedAsync();
        var deleted = false;
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName), beforeOpen: async _ =>
        {
            if (!deleted)
            {
                deleted = true;
                Assert.IsType<PhotoDeletionResult.Deleted>(await DeleteAsOwnerAsync(web, item));
            }
        });

        var result = await new PhotoReviewRejections(Factory(), store, _time)
            .RejectAsync(reviewer, item.CaseId, 2, PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct);

        Assert.True(deleted);
        Assert.IsType<PhotoReviewRejectionResult.NotFound>(result);
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Deleted'", item.UploadId));
    }

    [Fact]
    public async Task RejectingADeletedPhotoIsNotFound()
    {
        await using var web = ReviewWeb();
        var (reviewer, item) = await PreparedAsync();
        Assert.IsType<PhotoDeletionResult.Deleted>(await DeleteAsOwnerAsync(web, item));

        Assert.IsType<PhotoReviewRejectionResult.NotFound>(await new PhotoReviewRejections(Factory(),
            new FileSystemObjectStore(_root.FullName), _time).RejectAsync(reviewer, item.CaseId, 2,
            PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
    }

    /// <summary>Real API-M03 service with a fresh owner session, as the HTTP route calls it.</summary>
    private async Task<PhotoDeletionResult> DeleteAsOwnerAsync(DuoraWebFactory web, CaseData item)
    {
        var session = Guid.NewGuid();
        var now = _time.GetUtcNow();
        await ExecuteAsync("""
            insert into identity.login_sessions(id,user_id,key_hash,ticket,security_stamp,description,policy,
              created_at,last_seen_at,idle_expires_at,absolute_expires_at)
            select $2,u.id,$3,'\x01'::bytea,u.security_stamp,'synthetic','Normal',$4,$4,$5,$6 from identity.users u where u.id=$1
            """, item.OwnerId, session, SHA256.HashData(session.ToByteArray()), now, now.AddMinutes(10), now.AddHours(1));
        long version;
        await using (var db = Context())
        {
            version = await db.Database.SqlQuery<long>($"select version as \"Value\" from media.uploads where id={item.UploadId}").SingleAsync(Ct);
        }
        await using var scope = web.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhotoDeletions>()
            .DeleteAsync(item.OwnerId, session, item.UploadId, version, Key(), Ct);
    }

    private async Task<DateTimeOffset?> MarkerAsync(Guid generation)
    {
        await using var db = Context();
        return await db.Database.SqlQuery<DateTimeOffset?>(
            $"select cleanup_requested_at as \"Value\" from media.review_candidates where generation={generation}").SingleAsync(Ct);
    }
}
