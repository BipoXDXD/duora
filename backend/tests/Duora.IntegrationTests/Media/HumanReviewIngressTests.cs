using Duora.Core.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Media;

public sealed partial class MediaPipelineTests
{
    [Fact]
    public async Task HumanReviewPipelineStagesOnlyPrivateCandidatesAndSettlesTheTechnicalDelivery()
    {
        Assert.True(Enum.TryParse<MediaReviewPolicy>("HumanReview", out _));
        var (media, browser) = await SignInAsync();
        using var session = browser;
        var photoId = await UploadAsync(browser, TestImages.JpegWithGpsAndRotation(), "image/jpeg");
        var upload = Assert.Single(await media.UploadsOfAsync(await OwnerOfAsync(photoId)));
        var store = new CandidateWriteCountingStore(media.Store);
        var worker = StartWorker(services => services.AddSingleton<IObjectStore>(store), "--Media:Review=HumanReview");

        Assert.Equal(1, await DispatchOnceAsync(worker));

        Assert.Equal(("AwaitingReview", null), await StatusAsync(browser, photoId));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", photoId));
        Assert.Equal(1, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", photoId));
        Assert.Equal(1, await CountAsync("""
            select count(*) from ops.audit_events a join safety.photo_review_cases r on r.id=a.target_id
            where r.upload_id=$1 and a.action='safety.photo_review_requested'
            """, photoId));
        Assert.Equal(3, (await CandidateBytesAsync(media.Store, photoId)).Count);
        Assert.Equal(3, store.Writes);
        Assert.All(PhotoVariants.All, variant => Assert.False(File.Exists(DerivativePath(upload.QuarantineKey, variant))));
        Assert.Null(await media.ReadQuarantineAsync(upload));
        Assert.Equal("Delivered", await DeliveryStatusAsync(photoId));

        Assert.Equal(0, await DispatchOnceAsync(worker));
        Assert.Equal(3, store.Writes);
        Assert.Equal(1, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", photoId));
    }

    [Fact]
    public async Task HumanReviewPipelineRetainsOriginalAndDurableCleanupOnCandidateFailureThenRetriesPrivately()
    {
        Assert.True(Enum.TryParse<MediaReviewPolicy>("HumanReview", out _));
        var (media, browser) = await SignInAsync();
        using var session = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = Assert.Single(await media.UploadsOfAsync(await OwnerOfAsync(photoId)));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new CandidateWriteCountingStore(media.Store, failingPut: 2);
        var worker = StartWorker(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IObjectStore>(store);
        }, "--Media:Review=HumanReview");

        Assert.Equal(1, await DispatchOnceAsync(worker));

        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Equal(0, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", photoId));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", photoId));
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is null
              and cleanup_requested_at is not null
            """, photoId));
        Assert.All(PhotoVariants.All, variant => Assert.False(File.Exists(DerivativePath(upload.QuarantineKey, variant))));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await DispatchOnceAsync(worker));

        Assert.Equal(("AwaitingReview", null), await StatusAsync(browser, photoId));
        Assert.Null(await media.ReadQuarantineAsync(upload));
        Assert.Equal(1, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", photoId));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", photoId));
        Assert.Equal("Delivered", await DeliveryStatusAsync(photoId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanReviewPipelineCannotExtendItsLeaseOrTechnicalDeadlineAfterSanitizing(bool deliveryDeadlineFirst)
    {
        Assert.True(Enum.TryParse<MediaReviewPolicy>("HumanReview", out _));
        var (media, browser) = await SignInAsync();
        using var session = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = Assert.Single(await media.UploadsOfAsync(await OwnerOfAsync(photoId)));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        if (deliveryDeadlineFirst)
        {
            await media.ExecuteAsync("""
                update ops.outbox_deliveries d set deadline_at=$2 from ops.outbox_events e
                where d.outbox_event_id=e.id and e.resource_id=$1 and d.consumer='media-processing'
                """, photoId, clock.GetUtcNow().AddSeconds(30));
        }

        var store = new CandidateWriteCountingStore(media.Store);
        var worker = StartWorker(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IObjectStore>(store);
            services.AddSingleton<IPhotoSanitizer>(new DecidedMeanwhileSanitizer(() =>
            {
                clock.Advance(TimeSpan.FromMinutes(deliveryDeadlineFirst ? 1 : 3));
                return Task.CompletedTask;
            }));
        }, "--Media:Review=HumanReview");

        Assert.Equal(1, await DispatchOnceAsync(worker));

        Assert.Equal(("Processing", null), await StatusAsync(browser, photoId));
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, await CountAsync("select count(*) from media.review_candidates where upload_id=$1", photoId));
        Assert.Equal(0, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", photoId));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", photoId));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.All(PhotoVariants.All, variant => Assert.False(File.Exists(DerivativePath(upload.QuarantineKey, variant))));
    }
}
