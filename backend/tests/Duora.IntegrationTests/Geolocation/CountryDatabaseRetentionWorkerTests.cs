extern alias worker;

using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using worker::Duora.Worker;

namespace Duora.IntegrationTests.Geolocation;

public sealed class CountryDatabaseRetentionWorkerTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("clock")]
    public async Task UnverifiedLocalArtifactCannotAnnounceRecoveryOfAnUncreatedBackup(string scenario)
    {
        await using var h = await CountryDatabasePipelineTests.Harness.Create(postgres);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        var manifest = Assert.IsType<CountryDatabaseManifest>(await h.Store.ReadActiveAsync(Ct));
        var path = Path.Combine(h.Root, manifest.Sha256 + ".mmdb");
        if (scenario == "clock")
        {
            // Persist a future high-water mark, then restart with the earlier current clock.
            await using var mark = h.MaintenanceSource.CreateCommand("update ops.country_database_state set last_observed_at=$1 where singleton");
            mark.Parameters.AddWithValue(h.Time.GetUtcNow().AddMinutes(1));
            await mark.ExecuteNonQueryAsync(Ct);
        }
        else
        {
            File.Delete(path);
            if (scenario == "corrupt")
            { File.WriteAllBytes(path, [1, 2, 3]); }
        }
        var status = new CountryDatabaseOperationsStore(h.Source, h.Time);
        await status.RecordBackupFailureAsync(true, Ct);
        var delay = new DelayObserver(h.Time);
        using var worker = new CountryDatabaseRetentionWorker(
            new(new(h.MaintenanceSource, h.Time), h.Root, new EmptyArchive(), enabled: true), status,
            Options.Create(new CountryDatabaseRuntimeOptions()), delay, NullLogger<CountryDatabaseRetentionWorker>.Instance);
        await worker.StartAsync(Ct);
        try
        {
            await delay.IterationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.True((await status.ReadAsync(Ct)).BackupFailed);
        }
        finally { await worker.StopAsync(Ct); }
    }

    private sealed class DelayObserver(TimeProvider inner) : TimeProvider
    {
        internal TaskCompletionSource IterationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            IterationCompleted.TrySetResult();
            return timer;
        }
    }

    private sealed class EmptyArchive : ICountryDatabaseArchive
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task PutAsync(CountryDatabaseManifest manifest, Stream content, CancellationToken ct) => Task.CompletedTask;
        public Task<Stream?> OpenAsync(string hash, CancellationToken ct) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string hash, CancellationToken ct) => Task.CompletedTask;
    }
}
