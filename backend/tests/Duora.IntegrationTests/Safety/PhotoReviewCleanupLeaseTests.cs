using System.Security.Cryptography;
using Duora.Core.Identity;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Theory]
    [InlineData("approved", false, true)]
    [InlineData("approved", true, true)]
    [InlineData("abandoned", false, true)]
    [InlineData("abandoned", true, true)]
    [InlineData("abandoned", false, false)]
    [InlineData("abandoned", true, false)]
    public async Task ExistingCandidateCollectorsCannotAcknowledgeOrRetryALeaseThatExpiredDuringALockWait(
        string collector, bool retry, bool writerSettled)
    {
        var label = "candidate-existing-lease-" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = label };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        Guid generation;
        Func<CancellationToken, Task<bool>> finalize;
        Func<CancellationToken, Task<int>> dispatch;
        if (collector == "approved")
        {
            var (reviewer, item) = await PreparedAsync();
            Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
            generation = await CleanupGenerationAsync(item);
            var gc = ApprovedCleanup(source);
            var claim = Assert.IsType<ApprovedCandidateCleanupClaim>(await gc.ClaimAsync(Ct));
            finalize = ct => retry ? gc.RetryAsync(claim, ct) : gc.CompleteSweepAsync(claim, ct);
            dispatch = gc.DispatchOnceAsync;
        }
        else
        {
            generation = await SeedAbandonedLeaseCandidateAsync(writerSettled);
            var gc = new ReviewCandidateCleanupDispatcher(source, new FileSystemObjectStore(_root.FullName), _time,
                enabled: true, NullLogger<ReviewCandidateCleanupDispatcher>.Instance);
            var claim = Assert.IsType<ReviewCandidateCleanupClaim>(await gc.ClaimAsync(Ct));
            Assert.Equal(writerSettled, claim.WriterWasSettled);
            finalize = ct => retry ? gc.RetryAsync(claim, ct) : gc.CompleteSweepAsync(claim, ct);
            dispatch = gc.DispatchOnceAsync;
        }

        _time.Advance(TimeSpan.FromSeconds(59));
        var before = await ExpirationProjectionAsync();
        var files = Files();
        var retained = files.Where(file => !file.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)).ToArray();

        Assert.False(await AfterCleanupCandidateWaitAsync(generation, label, finalize,
            () => _time.Advance(TimeSpan.FromSeconds(2))));

        Assert.Equal(before, await ExpirationProjectionAsync());
        Assert.Equal(files, Files());
        Assert.Equal(1, await dispatch(Ct));
        Assert.Equal(retained, Files());
        Assert.Equal(writerSettled ? 1L : 0L, await ScalarAsync(
            "select count(*) from media.review_candidates where generation=$1 and deleted_at is not null", generation));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where generation=$1 and cleanup_token is null
              and cleanup_lease_until is null and last_cleanup_at is not null
            """, generation));
    }

    private async Task<Guid> SeedAbandonedLeaseCandidateAsync(bool writerSettled)
    {
        var owner = await ActorAsync(AccountRoles.Participant);
        var upload = Upload.Reserve(owner.UserId, Key(), PhotoFormat.Jpeg, 3, _time.GetUtcNow());
        await using (var db = Context())
        {
            db.Add(upload);
            await db.SaveChangesAsync(Ct);
        }

        var generation = Guid.NewGuid();
        var now = _time.GetUtcNow();
        byte[] partialBytes = [1, 2, 3];
        await ExecuteAsync("update media.uploads set state='Processing',version=2,content_sha256=$2 where id=$1",
            upload.Id, SHA256.HashData(partialBytes));
        await ExecuteAsync("""
            insert into media.review_candidates(generation,upload_id,upload_version,detected_type,width,height,
              created_at,prepare_deadline,cleanup_requested_at,writer_settled_at,cleanup_available_at)
            values($1,$2,2,'image/jpeg',1,1,$3,$4,$3,case when $5 then $3 else null end,$3)
            """, generation, upload.Id, now, now.AddMinutes(1), writerSettled);
        var store = new FileSystemObjectStore(_root.FullName);
        foreach (var kind in PhotoVariants.All)
        {
            var key = ObjectKey.ForReviewCandidate(generation, kind);
            await ExecuteAsync("""
                insert into media.review_candidate_variants(generation,kind,object_key,width,height,size_bytes,sha256)
                values($1,$2,$3,1,1,$4,$5)
                """, generation, PhotoVariants.Name(kind), key.Value, partialBytes.LongLength, SHA256.HashData(partialBytes));
            // Arbitrary partial private bytes model abandoned writes; no sanitization result is asserted here.
            using var content = new MemoryStream(partialBytes);
            await store.PutAsync(key, content, partialBytes.Length, "image/webp", Ct);
        }

        return generation;
    }
}
