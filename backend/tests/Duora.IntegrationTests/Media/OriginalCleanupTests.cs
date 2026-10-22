using System.Diagnostics;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

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

        Assert.Equal(1, await CountAsync("select count(*) from media.original_cleanup where upload_id = $1", photoId));
        Assert.True(_otherWorkers.Remove(first));
        first.Dispose();

        var sanitizer = new CountingSanitizer();
        var recovered = StartWorker(services => services.AddSingleton<IPhotoSanitizer>(sanitizer), "--Media:Review=");
        await recovered.StartAsync(Cancellation);
        try
        {
            var waiting = Stopwatch.StartNew();
            while ((await media.ReadQuarantineAsync(upload) is not null
                || await CountAsync("select count(*) from media.original_cleanup where upload_id = $1", photoId) != 0)
                && waiting.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), Cancellation);
            }

            Assert.Null(await media.ReadQuarantineAsync(upload));
            Assert.Equal(state, await StatusAsync(browser, photoId));
            Assert.Equal(0, sanitizer.Calls);
            Assert.Equal(0, await CountAsync("select count(*) from media.original_cleanup where upload_id = $1", photoId));
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

    [Fact]
    public async Task CleanupObligationRollsBackWithTheOutcomeAndNeverDeletesBeforeCommit()
    {
        var (media, browser) = await SignInAsync();
        using var browserLifetime = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = Assert.Single(await media.UploadsOfAsync(await OwnerOfAsync(photoId)));
        await using (var scope = Web.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OriginalCleanupQueue.EnqueueAsync(db, photoId, DateTimeOffset.UtcNow, Cancellation));
            await using var transaction = await db.Database.BeginTransactionAsync(Cancellation);
            await db.Database.ExecuteSqlAsync($"""
                update media.uploads set state='Rejected', rejection_code='CORRUPT_IMAGE', version=version+1
                where id={photoId}
                """, Cancellation);
            await OriginalCleanupQueue.EnqueueAsync(db, photoId, DateTimeOffset.UtcNow, Cancellation);
            Assert.NotNull(await media.ReadQuarantineAsync(upload));
            await transaction.RollbackAsync(Cancellation);
        }

        Assert.Equal(("Quarantined", null), await StatusAsync(browser, photoId));
        Assert.Equal(0, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1", photoId));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
    }

    [Fact]
    public async Task ImmediateDeletionLeavesNoPendingObligation()
    {
        var (media, browser) = await SignInAsync();
        using var browserLifetime = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = Assert.Single(await media.UploadsOfAsync(await OwnerOfAsync(photoId)));

        Assert.Equal(1, await DispatchOnceAsync());

        Assert.Null(await media.ReadQuarantineAsync(upload));
        Assert.Equal(0, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1", photoId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleCleanupOwnershipCannotAcknowledgeOrRetryAfterReclaim(bool recreate)
    {
        var (media, upload) = await LeaveUncollectedOriginalAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var cleanup = Cleanup(media.Store, clock);
        var first = Assert.IsType<OriginalCleanupClaim>(await cleanup.ClaimAsync(Cancellation));
        Assert.Null(await cleanup.ClaimAsync(Cancellation));
        if (recreate)
        {
            await media.Store.DeleteAsync(first.Key, Cancellation);
            Assert.True(await cleanup.CompleteAsync(first, Cancellation));
            await using var scope = Web.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Cancellation);
            await OriginalCleanupQueue.EnqueueAsync(db, upload.Id, clock.GetUtcNow(), Cancellation);
            await transaction.CommitAsync(Cancellation);
        }
        else
        {
            clock.Advance(OriginalCleanupDispatcher.LeaseDuration);
            Assert.False(await cleanup.CompleteAsync(first, Cancellation));
            Assert.False(await cleanup.RetryAsync(first, Cancellation));
        }

        var next = Assert.IsType<OriginalCleanupClaim>(await cleanup.ClaimAsync(Cancellation));
        Assert.NotEqual(first.Token, next.Token);
        Assert.False(await cleanup.CompleteAsync(first, Cancellation));
        Assert.False(await cleanup.RetryAsync(first, Cancellation));
        await media.Store.DeleteAsync(next.Key, Cancellation);
        Assert.True(await cleanup.CompleteAsync(next, Cancellation));
        Assert.Null(await media.ReadQuarantineAsync(upload));
    }

    [Fact]
    public async Task TwoCleanupConsumersOwnAtMostOneClaim()
    {
        var (media, _) = await LeaveUncollectedOriginalAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var first = Cleanup(media.Store, clock);
        var second = Cleanup(media.Store, clock);

        var claims = await Task.WhenAll(first.ClaimAsync(Cancellation), second.ClaimAsync(Cancellation));

        Assert.Single(claims.Where(claim => claim is not null));
    }

    [Fact]
    public async Task StorageWaitHoldsNoDatabaseLockAndCompletesAfterAnAlreadyAbsentOriginal()
    {
        var (media, upload) = await LeaveUncollectedOriginalAsync();
        var blocked = new BlockingOriginalStore(media.Store);
        var cleanup = Cleanup(blocked, new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1)));
        var dispatch = cleanup.DispatchOnceAsync(Cancellation);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
        try
        {
            await using var connection = await OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(Cancellation);
            await using var setup = new NpgsqlCommand("set local lock_timeout='200ms'", connection, transaction);
            await setup.ExecuteNonQueryAsync(Cancellation);
            await using var command = new NpgsqlCommand(
                "update media.original_cleanup set available_at=available_at where upload_id=$1", connection, transaction);
            command.Parameters.AddWithValue(upload.Id);
            await command.ExecuteNonQueryAsync(Cancellation);
            await transaction.CommitAsync(Cancellation);
            await media.Store.DeleteAsync(ObjectKey.Parse(upload.QuarantineKey), Cancellation);
        }
        finally
        {
            blocked.Release.TrySetResult();
        }

        Assert.Equal(1, await dispatch);
        Assert.Equal(0, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1", upload.Id));
    }

    [Fact]
    public async Task ShutdownDuringDeletionKeepsTheLeaseForRecovery()
    {
        var (media, upload) = await LeaveUncollectedOriginalAsync();
        var blocked = new BlockingOriginalStore(media.Store);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var cleanup = Cleanup(blocked, clock);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var dispatch = cleanup.DispatchOnceAsync(shutdown.Token);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Null(await cleanup.ClaimAsync(Cancellation));
        clock.Advance(OriginalCleanupDispatcher.LeaseDuration);
        Assert.Equal(1, await Cleanup(media.Store, clock).DispatchOnceAsync(Cancellation));
        Assert.Null(await media.ReadQuarantineAsync(upload));
    }

    [Fact]
    public async Task SlowDeletionIsBoundedBelowItsLeaseAndRetainsTheObligation()
    {
        var (media, upload) = await LeaveUncollectedOriginalAsync();
        var blocked = new BlockingOriginalStore(media.Store);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var cleanup = Cleanup(blocked, clock);
        var elapsed = Stopwatch.StartNew();

        Assert.Equal(1, await cleanup.DispatchOnceAsync(Cancellation));

        Assert.True(elapsed.Elapsed < OriginalCleanupDispatcher.LeaseDuration);
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Null(await cleanup.ClaimAsync(Cancellation));
        Assert.Equal(1, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1 and lease_until is null", upload.Id));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(1, await Cleanup(media.Store, clock).DispatchOnceAsync(Cancellation));
    }

    [Fact]
    public async Task FailedDeletionNeverExpiresTheObligationAndUnsetStorageDoesNotConsumeIt()
    {
        var (media, upload) = await LeaveUncollectedOriginalAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var cleanup = Cleanup(new OriginalDeletionRefusingStore(media.Store), clock);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            Assert.Equal(1, await cleanup.DispatchOnceAsync(Cancellation));
            clock.Advance(TimeSpan.FromMinutes(15));
        }

        Assert.Equal(1, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1 and attempts=12", upload.Id));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Equal(0, await Cleanup(media.Store, clock, enabled: false).DispatchOnceAsync(Cancellation));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Equal(1, await Cleanup(media.Store, clock).DispatchOnceAsync(Cancellation));
    }

    [Fact]
    public async Task CleanupNeverScansLegacyOriginalsOrDeletesAnUnrelatedUpload()
    {
        var (media, browser) = await SignInAsync();
        using var browserLifetime = browser;
        var pending = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = Assert.Single(await media.UploadsOfAsync(await OwnerOfAsync(pending)));
        var original = await media.ReadQuarantineAsync(upload);
        await ExecuteAsync("update media.uploads set state='Approved' where id=$1", pending);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var cleanup = Cleanup(media.Store, clock);

        Assert.Equal(0, await cleanup.DispatchOnceAsync(Cancellation));
        Assert.Equal(original, await media.ReadQuarantineAsync(upload));
        var otherId = await UploadAsync(browser, TestImages.Png(), "image/png");
        var other = (await media.UploadsOfAsync(await OwnerOfAsync(otherId))).Single(row => row.Id == otherId);
        var otherBytes = await media.ReadQuarantineAsync(other);
        await media.ExecuteAsync("""
            insert into media.original_cleanup(upload_id,object_key,created_at,available_at)
            values($1,$2,$3,$3)
            """, pending, other.QuarantineKey, clock.GetUtcNow());
        Assert.Equal(0, await cleanup.DispatchOnceAsync(Cancellation));
        Assert.Equal(original, await media.ReadQuarantineAsync(upload));
        Assert.Equal(otherBytes, await media.ReadQuarantineAsync(other));
    }

    private OriginalCleanupDispatcher Cleanup(IObjectStore store, TimeProvider clock, bool enabled = true) => new(
        Web.Services.GetRequiredService<NpgsqlDataSource>(), store, clock, enabled, NullLogger<OriginalCleanupDispatcher>.Instance);

    private async Task<(MediaScenario Media, StoredUpload Upload)> LeaveUncollectedOriginalAsync()
    {
        var (media, browser) = await SignInAsync();
        using var browserLifetime = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = Assert.Single(await media.UploadsOfAsync(await OwnerOfAsync(photoId)));
        var refusing = StartWorker(services => services.AddSingleton<IObjectStore>(new OriginalDeletionRefusingStore(media.Store)));
        Assert.Equal(1, await DispatchOnceAsync(refusing));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Equal(1, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1", photoId));
        return (media, upload);
    }

    private sealed class BlockingOriginalStore(IObjectStore inner) : IObjectStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, contentType, cancellationToken);

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, cancellationToken);

        public async Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await inner.DeleteAsync(key, cancellationToken);
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
