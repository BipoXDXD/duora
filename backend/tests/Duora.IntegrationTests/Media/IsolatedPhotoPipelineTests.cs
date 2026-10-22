using Duora.Core.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.IntegrationTests.Media;

public sealed partial class MediaPipelineTests
{
    [Fact]
    public async Task WorkerShutdownLeavesAnUnacknowledgedLeaseAndTheNextWorkerRecovers()
    {
        var (_, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        _launcher.SetMode("hold");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var pending = DispatchOnceAsync(_worker!, shutdown.Token);
        await _launcher.WaitForAsync("started", Cancellation);
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("InFlight", await DeliveryStatusAsync(photoId));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
        await ExpireMediaLeaseAsync(photoId);
        _launcher.SetMode("normal");
        Assert.Equal(1, await DispatchOnceAsync(StartWorker(services => { })));
        Assert.Equal("Delivered", await DeliveryStatusAsync(photoId));
        Assert.Equal(1, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
    }

    [Theory]
    [InlineData("crash")]
    [InlineData("after-output")]
    [InlineData("truncated")]
    [InlineData("cleanup-blocked")]
    public async Task LauncherFailuresKeepTheOriginalAndRetryWithoutPublishingOrAcknowledging(string mode)
    {
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = (await media.UploadsOfAsync(await OwnerOfAsync(photoId))).Single();
        _launcher.SetMode(mode);

        Assert.Equal(1, await DispatchOnceAsync());

        Assert.Equal(("Processing", null), await StatusAsync(browser, photoId));
        Assert.Equal("Pending", await DeliveryStatusAsync(photoId));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.All(PhotoVariants.All, variant => Assert.False(File.Exists(DerivativePath(upload.QuarantineKey, variant))));
    }

    [Theory]
    [InlineData("hold", "started")]
    [InlineData("cleanup-delay", "cleanup")]
    public async Task AStaleAttemptDuringExecutionOrCleanupCannotPublishAndAFreshWorkerRecovers(string mode, string marker)
    {
        var (_, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        _launcher.SetMode(mode);
        var pending = DispatchOnceAsync();
        await _launcher.WaitForAsync(marker, Cancellation);
        Assert.Equal("InFlight", await DeliveryStatusAsync(photoId));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
        await ExecuteAsync("update media.uploads set version = version + 1 where id = $1", photoId);
        await ExpireMediaLeaseAsync(photoId);
        _launcher.SetMode("normal");
        Assert.Equal(1, await pending);
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
        Assert.Equal("InFlight", await DeliveryStatusAsync(photoId));

        var restarted = StartWorker(services => { });
        Assert.Equal(1, await DispatchOnceAsync(restarted));
        Assert.Equal(("Approved", null), await StatusAsync(browser, photoId));
        Assert.Equal(1, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
        Assert.Equal("Delivered", await DeliveryStatusAsync(photoId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseFailureBeforeOrAfterAssetCommitRecoversWithoutDuplicatePublication(bool afterCommit)
    {
        var (_, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        // Only fault-injection DDL uses the schema owner; the Worker keeps its restricted app role.
        var migrator = new NpgsqlConnectionStringBuilder(postgres.MigratorConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(_connectionString).Database,
        };
        await using var connection = new NpgsqlConnection(migrator.ConnectionString);
        await connection.OpenAsync(Cancellation);
        var table = afterCommit ? "ops.outbox_deliveries" : "media.media_assets";
        var condition = afterCommit ? "if NEW.status = 'Delivered' then raise exception 'Synthetic settle failure'; end if;" : "raise exception 'Synthetic asset failure';";
        await using (var install = new NpgsqlCommand($"""
            create function public.fail_media_s3() returns trigger language plpgsql as $$
            begin {condition} return NEW; end $$;
            create trigger fail_media_s3 before {(afterCommit ? "update" : "insert")} on {table}
            for each row execute function public.fail_media_s3();
            """, connection))
        {
            await install.ExecuteNonQueryAsync(Cancellation);
        }
        if (afterCommit)
        {
            await Assert.ThrowsAsync<PostgresException>(() => DispatchOnceAsync());
            Assert.Equal(("Approved", null), await StatusAsync(browser, photoId));
            Assert.Equal("InFlight", await DeliveryStatusAsync(photoId));
        }
        else
        {
            Assert.Equal(1, await DispatchOnceAsync());
            Assert.Equal(("Processing", null), await StatusAsync(browser, photoId));
            Assert.Equal("Pending", await DeliveryStatusAsync(photoId));
        }
        Assert.Equal(afterCommit ? 1 : 0, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
        await using (var remove = new NpgsqlCommand($"drop trigger fail_media_s3 on {table}; drop function public.fail_media_s3();", connection))
        {
            await remove.ExecuteNonQueryAsync(Cancellation);
        }
        if (afterCommit)
        {
            await ExpireMediaLeaseAsync(photoId);
        }
        else
        {
            // The failed asset transaction was already retried: Pending has no lease to expire.
            await ExecuteAsync("""
                update ops.outbox_deliveries set available_at = now() - interval '1 minute'
                where outbox_event_id in (select id from ops.outbox_events where resource_id = $1)
                """, photoId);
        }
        var restarted = StartWorker(services => { });
        Assert.Equal(1, await DispatchOnceAsync(restarted));
        Assert.Equal(1, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
        Assert.Equal("Delivered", await DeliveryStatusAsync(photoId));
    }

    private Task ExpireMediaLeaseAsync(Guid photoId) => ExecuteAsync("""
        update ops.outbox_deliveries set lease_expires_at = now() - interval '1 minute',
        available_at = now() - interval '1 minute'
        where outbox_event_id in (select id from ops.outbox_events where resource_id = $1)
        """, photoId);

    [Fact]
    public async Task CleanupFailureAfterCommitKeepsOneAssetAndReportsTheOrphanWithoutReencoding()
    {
        var logs = new CapturingLoggerProvider();
        var failing = StartWorker(services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            var real = services.Single(descriptor => descriptor.ServiceType == typeof(IObjectStore));
            services.Remove(real);
            services.AddSingleton<IObjectStore>(provider =>
                new OriginalDeleteRefusingStore((IObjectStore)real.ImplementationFactory!(provider)));
        });
        var (media, browser) = await SignInAsync();
        using var _ = browser;
        var photoId = await UploadAsync(browser, TestImages.Jpeg(), "image/jpeg");
        var upload = (await media.UploadsOfAsync(await OwnerOfAsync(photoId))).Single();
        Assert.Equal(1, await DispatchOnceAsync(failing));
        Assert.Equal("Delivered", await DeliveryStatusAsync(photoId));
        Assert.Equal(("Approved", null), await StatusAsync(browser, photoId));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Null((await ReadAssetAsync(photoId)).OriginalKey);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Error
            && entry.Text.Contains("não foi apagado", StringComparison.Ordinal));
        Assert.Equal(0, await DispatchOnceAsync(failing));
        Assert.Equal(1, _launcher.AdmissionCount);
        Assert.Equal(1, await CountAsync("select count(*) from media.media_assets where upload_id = $1", photoId));
    }

    private sealed class OriginalDeleteRefusingStore(IObjectStore inner) : IObjectStore
    {
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, contentType, cancellationToken);

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            key.Value.StartsWith("quarantine/", StringComparison.Ordinal)
                ? throw new IOException("Synthetic original cleanup failure.")
                : inner.DeleteAsync(key, cancellationToken);
    }
}
