using System.Net;
using Duora.Core.Identity;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Duora.IntegrationTests.Media;
using Duora.Web.Safety;
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
}
