using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.IntegrationTests.Database;

public sealed class MigratorConnectionLifetimeTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingAnEphemeralMigratorReleasesItsServerConnection(bool useContext)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await postgres.CreateEmptyDatabaseAsync(ct);
        int pid;
        if (useContext)
        {
            await using var context = PostgresFixture.CreateDbContext(database.MigratorConnectionString);
            await context.Database.OpenConnectionAsync(ct);
            pid = ((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
        }
        else
        {
            await using var migrator = new NpgsqlConnection(database.MigratorConnectionString);
            await migrator.OpenAsync(ct);
            pid = migrator.ProcessID;
        }
        await using var observer = new NpgsqlConnection(postgres.MigratorConnectionString);
        await observer.OpenAsync(ct);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            await using var check = new NpgsqlCommand("select exists(select 1 from pg_stat_activity where pid=@pid)", observer);
            check.Parameters.AddWithValue("pid", pid);
            if (!(bool)(await check.ExecuteScalarAsync(ct))!)
            { break; }
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(1), "Disposed test migrator retained an idle server connection.");
            await Task.Delay(10, ct);
        }
    }

    [Fact]
    public async Task ApplicationConnectionsStillReuseTheirPool()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await postgres.CreateEmptyDatabaseAsync(ct);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        int pid;
        await using (var first = await source.OpenConnectionAsync(ct))
        {
            pid = first.ProcessID;
        }
        await using var second = await source.OpenConnectionAsync(ct);
        Assert.Equal(pid, second.ProcessID);
    }

    [Fact]
    public async Task ReturnedScratchApplicationConnectionsReleaseTheirBackendsWhileLeasedConnectionsStayUsable()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await postgres.CreateEmptyDatabaseAsync(ct);
        var settings = new NpgsqlConnectionStringBuilder(database.AppConnectionString);
        Assert.True(settings.Pooling);
        Assert.Equal(400, PostgresFixture.MaxConnections);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var leased = await source.OpenConnectionAsync(ct);
        int returnedPid;
        await using (var returned = await source.OpenConnectionAsync(ct))
        {
            returnedPid = returned.ProcessID;
            Assert.NotEqual(leased.ProcessID, returnedPid);
        }

        await using var observer = new NpgsqlConnection(postgres.MigratorConnectionString);
        await observer.OpenAsync(ct);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            await using var check = new NpgsqlCommand("select exists(select 1 from pg_stat_activity where pid=$1)", observer);
            check.Parameters.AddWithValue(returnedPid);
            if (!(bool)(await check.ExecuteScalarAsync(ct))!)
            {
                break;
            }

            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15),
                "Returned scratch application connections retained idle backends across unrelated tests.");
            await Task.Delay(50, ct);
        }

        await using var active = new NpgsqlCommand("select pg_backend_pid()", leased);
        Assert.Equal(leased.ProcessID, (int)(await active.ExecuteScalarAsync(ct))!);
        await using var replacement = await source.OpenConnectionAsync(ct);
        Assert.NotEqual(returnedPid, replacement.ProcessID);
    }
}
