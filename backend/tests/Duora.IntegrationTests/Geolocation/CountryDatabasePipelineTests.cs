using System.Net;
using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Geolocation;

public sealed class CountryDatabasePipelineTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteCommittedArtifactSurvivesRestartAndLookupMakesNoNetworkCall()
    {
        await using var h = await Harness.Create(postgres);
        using var active = new ActiveCountryDatabase(h.Root, h.Validator, h.Time);
        Assert.False(await active.RefreshAsync(h.Store, Ct));
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        var m = Assert.IsType<CountryDatabaseManifest>(await h.Store.ReadActiveAsync(Ct));
        Assert.True(File.Exists(Path.Combine(h.Root, m.Sha256 + ".mmdb")));
        using var restarted = new ActiveCountryDatabase(h.Root, h.Validator, h.Time);
        var restartedStore = new CountryDatabaseUpdateStore(h.Source, h.Time, TimeSpan.FromMinutes(2));
        Assert.True(await restarted.RefreshAsync(restartedStore, Ct));
        Assert.Equal("BR", restarted.Lookup(IPAddress.Parse("192.0.2.1")).CountryCode);
        Assert.Equal(1, h.Calls);
        Assert.Empty(Directory.GetDirectories(h.Root));
    }

    [Fact]
    public async Task FirstArtifactWithoutApprovalRetainsOnlyMetadataAndNeverBecomesActive()
    {
        await using var h = await Harness.Create(postgres, approve: false);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Failed, await h.Service.ExecuteAsync(lease, Ct));
        Assert.Null(await h.Store.ReadActiveAsync(Ct));
        Assert.Equal(1, await Count(h.Source, "select count(*) from ops.country_database_versions"));
        Assert.Empty(Directory.GetFiles(h.Root));
        h.Time.Advance(TimeSpan.FromDays(1));
        Assert.Null(await h.Store.ClaimAsync(Ct));
        Assert.Empty(Directory.GetDirectories(h.Root));
    }

    [Fact]
    public async Task TruncatedUpdatePreservesThePreviousActiveVersionAndDeletesTemporaryFiles()
    {
        await using var h = await Harness.Create(postgres);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        var previous = await h.Store.ReadActiveAsync(Ct);
        h.Time.Advance(TimeSpan.FromDays(1));
        h.Payload = [0, 1];
        lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Failed, await h.Service.ExecuteAsync(lease, Ct));
        Assert.Equal(previous, await h.Store.ReadActiveAsync(Ct));
        Assert.Equal(1, await Count(h.Source, "select count(*) from ops.country_database_versions"));
        Assert.Empty(Directory.GetDirectories(h.Root));
    }

    [Fact]
    public async Task PausedOldDownloaderCannotPublishAfterReplacementWorkerPromotes()
    {
        await using var h = await Harness.Create(postgres);
        var oldPayload = h.Payload;
        var newPayload = SyntheticMmdb.Create(Now.AddHours(-1), country: "US");
        var expected = h.Validator.Validate(newPayload, Now, Ct);
        h.RebuildService(expected.Sha256);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Send = async (_, ct) =>
        {
            if (h.Calls == 1)
            {
                started.SetResult();
                await resume.Task.WaitAsync(ct);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(oldPayload) };
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(newPayload) };
        };
        var old = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        var work = h.Service.ExecuteAsync(old, Ct);
        await started.Task.WaitAsync(Ct);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        var current = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(current, Ct));
        resume.SetResult();
        Assert.Equal(CountryDatabaseUpdateOutcome.LostLease, await work);
        Assert.Equal(expected.Sha256, (await h.Store.ReadActiveAsync(Ct))!.Sha256);
        Assert.Empty(Directory.GetDirectories(h.Root));
    }

    [Fact]
    public async Task RestartBeforePublicationKeepsOriginalDownloadAndMissingCommittedFileIsUnavailable()
    {
        await using var h = await Harness.Create(postgres);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        var manifest = h.Validator.Validate(h.Payload, Now, Ct);
        Assert.Equal(manifest, await h.Store.RecordCandidateAsync(lease, manifest, Ct));
        Assert.Null(await h.Store.ReadActiveAsync(Ct));
        using (var before = new ActiveCountryDatabase(h.Root, h.Validator, h.Time))
        {
            Assert.False(await before.RefreshAsync(h.Store, Ct));
        }
        h.Time.Advance(TimeSpan.FromMinutes(2));
        var restartedStore = new CountryDatabaseUpdateStore(h.Source, h.Time, TimeSpan.FromMinutes(2));
        lease = Assert.IsType<CountryDatabaseUpdateLease>(await restartedStore.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        Assert.Equal(manifest, await restartedStore.ReadActiveAsync(Ct));
        File.Delete(Path.Combine(h.Root, manifest.Sha256 + ".mmdb"));
        using var after = new ActiveCountryDatabase(h.Root, h.Validator, h.Time);
        Assert.False(await after.RefreshAsync(restartedStore, Ct));
        Assert.Equal(CountryLookupStatus.Unavailable, after.Lookup(IPAddress.Parse("192.0.2.1")).Status);
    }

    private static async Task<long> Count(NpgsqlDataSource source, string sql)
    {
        await using var q = source.CreateCommand(sql);
        return (long)(await q.ExecuteScalarAsync(Ct))!;
    }

    internal sealed class Harness : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "duora-pipeline-" + Guid.NewGuid().ToString("N"));
        public NpgsqlDataSource Source { get; }
        public NpgsqlDataSource MaintenanceSource { get; }
        public FakeTimeProvider Time { get; } = new(Now);
        public CountryDatabaseUpdateStore Store { get; }
        public CountryDatabaseArtifactValidator Validator { get; }
        public CountryDatabaseDownloader Downloader { get; }
        public CountryDatabaseUpdateService Service { get; private set; }
        public byte[] Payload { get; set; } = SyntheticMmdb.Create(Now.AddHours(-1));
        public int Calls { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Send { get; set; }
        private Harness(NpgsqlDataSource source, NpgsqlDataSource maintenanceSource, bool approve)
        {
            Source = source;
            MaintenanceSource = maintenanceSource;
            Store = new(source, Time, TimeSpan.FromMinutes(2));
            Validator = new(new(SyntheticMmdb.DatabaseType, 1024 * 1024, 100), Time);
            Downloader = new(new Handler(this), new(1024 * 1024, TimeSpan.FromMinutes(10), new HashSet<string>(StringComparer.Ordinal)), "synthetic-token", Time);
            Service = new(Store, Downloader, Validator, Root, approve ? Validator.Validate(Payload, Now, Ct).Sha256 : null);
        }
        public void RebuildService(string approvedHash) => Service = new(Store, Downloader, Validator, Root, approvedHash);
        public static async Task<Harness> Create(PostgresFixture postgres, bool approve = true)
        {
            var app = await postgres.CreateMigratedDatabaseAsync(Ct);
            var maintenance = new NpgsqlConnectionStringBuilder(postgres.MigratorConnectionString)
            { Database = new NpgsqlConnectionStringBuilder(app).Database };
            return new(NpgsqlDataSource.Create(app), NpgsqlDataSource.Create(maintenance.ConnectionString), approve);
        }
        public async ValueTask DisposeAsync()
        {
            Downloader.Dispose();
            await Source.DisposeAsync();
            await MaintenanceSource.DisposeAsync();
            if (Directory.Exists(Root))
            { Directory.Delete(Root, true); }
        }
        private sealed class Handler(Harness harness) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                harness.Calls++;
                return harness.Send?.Invoke(request, cancellationToken) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(harness.Payload) });
            }
        }
    }
}
