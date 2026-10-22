using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Media;

public sealed partial class MediaPipelineTests
{
    [Fact]
    public async Task SanitizedCandidateCommitsReviewCaseAndOriginalCleanupWithoutAnApprovedAsset()
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var store = new OriginalDeletionRefusingStore(media.Store);

        var result = Assert.IsType<ReviewCandidateStageResult.Staged>(await StageCandidateAsync(
            upload.Id, version, store, DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Equal(1, await CountAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview'", upload.Id));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from safety.photo_review_cases where id=$1 and policy_version='human-review-v1'
              and review_deadline=awaiting_review_at+interval '72 hours' and version=1
            """, result.CaseId));
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates c join safety.photo_review_cases r
              on r.id=c.case_id and r.upload_id=c.upload_id and r.candidate_generation=c.generation
            where c.upload_id=$1 and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null
            """, upload.Id));
        Assert.Equal(3, await CountAsync("""
            select count(*) from media.review_candidate_variants v join media.review_candidates c using(generation)
            where c.upload_id=$1 and octet_length(v.sha256)=32
            """, upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from ops.audit_events where target_id=$1 and target_type='photo_review_case'
              and action='safety.photo_review_requested' and actor_kind='System' and target_version=1
            """, result.CaseId));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Equal(1, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1", upload.Id));
        Assert.Equal("Pending", await DeliveryStatusAsync(upload.Id));
    }

    [Fact]
    public async Task ConfirmedStageReplayReturnsTheSameCaseWithoutNewWritesOrAudit()
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var store = new CandidateWriteCountingStore(media.Store);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        var first = Assert.IsType<ReviewCandidateStageResult.Staged>(await StageCandidateAsync(upload.Id, version, store, deadline));
        Assert.Equal(3, store.Writes);

        var replay = Assert.IsType<ReviewCandidateStageResult.Staged>(await StageCandidateAsync(upload.Id, version, store, deadline));

        Assert.Equal(first, replay);
        Assert.Equal(3, store.Writes);
        Assert.Equal(1, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_requested'
            """, first.CaseId));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
        Assert.Null(await media.ReadQuarantineAsync(upload));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AFailedCandidatePutKeepsDurableCleanupAndNeverCreatesACase(int failingPut)
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var store = new CandidateWriteCountingStore(media.Store, failingPut);

        await Assert.ThrowsAsync<IOException>(() => StageCandidateAsync(upload.Id, version, store, DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Equal(0, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
        Assert.Equal(1, await CountAsync("select count(*) from media.uploads where id=$1 and state='Processing'", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is null
              and writer_settled_at is null and cleanup_requested_at is not null
            """, upload.Id));
        Assert.Equal(3, await CountAsync("""
            select count(*) from media.review_candidate_variants v join media.review_candidates c using(generation)
            where c.upload_id=$1
            """, upload.Id));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Equal(0, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1", upload.Id));
    }

    private async Task<(MediaScenario Media, StoredUpload Upload, long Version)> ReviewReadyUploadAsync()
    {
        var (media, browser) = await SignInAsync();
        using var browserLifetime = browser;
        var id = await UploadAsync(browser, TestImages.Jpeg(64, 32), "image/jpeg");
        var upload = Assert.Single(await media.UploadsOfAsync(await OwnerOfAsync(id)));
        await ExecuteAsync("update media.uploads set state='Processing',version=version+1 where id=$1", id);
        await using var scope = Web.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
        var version = await db.Set<Upload>().Where(row => row.Id == id).Select(row => row.Version).SingleAsync(Cancellation);
        return (media, upload, version);
    }

    private async Task<ReviewCandidateStageResult> StageCandidateAsync(
        Guid uploadId, long version, IObjectStore store, DateTimeOffset deadline)
    {
        await using var scope = Web.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
        var stage = new PhotoReviewCandidates(db, store, TimeProvider.System, new MediaProcessingOptions());
        var sanitized = new SanitizeOutcome.Sanitized(PhotoFormat.Jpeg, 64, 32,
            [.. PhotoVariants.All.Select(kind => new SanitizedVariant(kind, TestImages.WebP(64, 32), 64, 32))]);
        return await stage.StageAsync(uploadId, version, deadline, sanitized, Cancellation);
    }

    private sealed class CandidateWriteCountingStore(IObjectStore inner, int? failingPut = null) : IObjectStore
    {
        internal int Writes { get; private set; }

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            if (key.Area == StorageArea.ReviewCandidates && ++Writes == failingPut)
            {
                throw new IOException("Synthetic candidate storage failure");
            }

            return inner.PutAsync(key, content, length, contentType, cancellationToken);
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
    }
}
