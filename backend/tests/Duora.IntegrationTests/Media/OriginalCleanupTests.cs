using System.Diagnostics;
using Duora.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Media;

public sealed partial class MediaPipelineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommittedOriginalDeletionIsRetriedAfterRestartWithUploadsDisabled(bool approved)
    {
        var (media, browser) = await SignInAsync();
        using var browserLifetime = browser;
        var photoId = await UploadAsync(browser, TestImages.JpegWithGpsAndRotation(), "image/jpeg");
        var upload = Assert.Single(await media.UploadsOfAsync(await OwnerOfAsync(photoId)));
        var rejectingStore = new OriginalDeletionRefusingStore(media.Store);
        var first = StartWorker(services =>
        {
            services.AddSingleton<IObjectStore>(rejectingStore);
            if (!approved)
            {
                services.AddSingleton<IPhotoSanitizer>(new CountingSanitizer());
            }
        });

        Assert.Equal(1, await DispatchOnceAsync(first));
        var state = await StatusAsync(browser, photoId);
        Assert.Equal(approved ? "Approved" : "Rejected", state.Status);
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        var derivatives = new Dictionary<PhotoVariant, byte[]>();
        if (approved)
        {
            foreach (var variant in PhotoVariants.All)
            {
                derivatives[variant] = await ReadDerivativeAsync(media, upload.QuarantineKey, variant);
            }
        }

        var sanitizer = new CountingSanitizer();
        var recovered = StartWorker(services => services.AddSingleton<IPhotoSanitizer>(sanitizer), "--Media:Review=");
        await recovered.StartAsync(Cancellation);
        try
        {
            var waiting = Stopwatch.StartNew();
            while (await media.ReadQuarantineAsync(upload) is not null && waiting.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), Cancellation);
            }

            Assert.Null(await media.ReadQuarantineAsync(upload));
            Assert.Equal(state, await StatusAsync(browser, photoId));
            Assert.Equal(0, sanitizer.Calls);
            Assert.Equal(approved ? 1 : 0, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
            Assert.Equal("Delivered", await DeliveryStatusAsync(photoId));
            foreach (var (variant, bytes) in derivatives)
            {
                Assert.Equal(bytes, await ReadDerivativeAsync(media, upload.QuarantineKey, variant));
            }
        }
        finally
        {
            await recovered.StopAsync(Cancellation);
        }
    }

    private sealed class OriginalDeletionRefusingStore(IObjectStore inner) : IObjectStore
    {
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, contentType, cancellationToken);

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            key.Area == StorageArea.Quarantine
                ? throw new IOException("Synthetic original deletion failure")
                : inner.DeleteAsync(key, cancellationToken);
    }
}
