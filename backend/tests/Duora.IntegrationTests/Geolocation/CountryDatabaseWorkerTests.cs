extern alias worker;
using Duora.Infrastructure.Geolocation;
using worker::Duora.Worker;

namespace Duora.IntegrationTests.Geolocation;

public sealed class CountryDatabaseWorkerTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task DisabledByDefaultCreatesNoJobsFilesOrNetworkRequests()
    {
        await using var h = await CountryDatabasePipelineTests.Harness.Create(postgres);
        using var worker = new CountryDatabaseUpdateWorker(h.Store, h.Service, h.Time, Lease);
        Assert.False(await worker.RunOnceAsync(Ct));
        Assert.Equal(0, h.Calls); Assert.False(Directory.Exists(h.Root));
        await using var q = h.Source.CreateCommand("select count(*) from ops.jobs where kind='country-database-update'");
        Assert.Equal(0L, await q.ExecuteScalarAsync(Ct));
        using var active = new ActiveCountryDatabase(h.Root, h.Validator, h.Time);
        Assert.False(new MmdbCountryLookup(active).Enabled);
    }

    [Fact]
    public async Task RenewalKeepsOwnershipDuringExternalWorkWithoutHoldingSqlLocks()
    {
        await using var h = await CountryDatabasePipelineTests.Harness.Create(postgres);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Send = async (_, ct) =>
        {
            started.SetResult(); await resume.Task.WaitAsync(ct);
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(h.Payload) };
        };
        using var worker = new CountryDatabaseUpdateWorker(h.Store, h.Service, h.Time, Lease, enabled: true);
        var began = h.Time.GetUtcNow();
        var work = worker.RunOnceAsync(Ct); await started.Task.WaitAsync(Ct);
        h.Time.Advance(TimeSpan.FromSeconds(40));
        var until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            await using var q = h.Source.CreateCommand("select lease_expires_at from ops.jobs where kind='country-database-update'");
            if (new DateTimeOffset((DateTime)(await q.ExecuteScalarAsync(Ct))!) > began + Lease) { break; }
            Assert.True(DateTimeOffset.UtcNow < until, "Renewal must finish while the synthetic HTTP request remains paused.");
            await Task.Delay(10, Ct);
        }
        h.Time.Advance(TimeSpan.FromSeconds(81));
        Assert.Null(await h.Store.ClaimAsync(Ct));
        resume.SetResult(); Assert.True(await work);
        Assert.NotNull(await h.Store.ReadActiveAsync(Ct));
    }

    [Fact]
    public async Task LostRenewalCancelsExternalWorkAndCannotPublish()
    {
        await using var h = await CountryDatabasePipelineTests.Harness.Create(postgres);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Send = async (_, ct) =>
        {
            started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(System.Net.HttpStatusCode.OK);
        };
        using var worker = new CountryDatabaseUpdateWorker(h.Store, h.Service, h.Time, Lease, enabled: true);
        var work = worker.RunOnceAsync(Ct); await started.Task.WaitAsync(Ct);
        await using (var q = h.Source.CreateCommand("update ops.jobs set lease_expires_at=$1 where kind='country-database-update'"))
        {
            q.Parameters.AddWithValue(h.Time.GetUtcNow()); await q.ExecuteNonQueryAsync(Ct);
        }
        h.Time.Advance(TimeSpan.FromSeconds(40));
        Assert.True(await work.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Null(await h.Store.ReadActiveAsync(Ct));
        Assert.Empty(Directory.GetDirectories(h.Root));
    }
}
