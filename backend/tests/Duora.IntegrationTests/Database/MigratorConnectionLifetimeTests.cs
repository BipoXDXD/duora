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
}
