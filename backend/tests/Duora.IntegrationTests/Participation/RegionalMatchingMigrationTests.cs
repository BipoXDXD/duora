using Duora.Infrastructure.Participation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

public sealed class RegionalMatchingMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ExpansionPreservesV2HistoryAndRequiresPairVersionToMatchItsRound()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await postgres.CreateEmptyDatabaseAsync(ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        { await before.GetService<IMigrator>().MigrateAsync("20261021160000_RegionalPreferences", cancellationToken: ct); }
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(database.AppConnectionString, now, ct, Guid.NewGuid().ToString("N"), true);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            await service.RegisterAsync(seed.Events[0], user, false, ct);
            await service.RegisterAsync(seed.Events[0], user, true, ct);
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(seed.Rounds[0], ct);
        await using var connection = await source.OpenConnectionAsync(ct);
        var oldRounds = await Snapshot(connection, "events.rounds", ct);
        var oldPairs = await Snapshot(connection, "events.pairings", ct);
        await using (var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        { await after.Database.MigrateAsync(ct); }
        Assert.Equal(oldRounds, await Snapshot(connection, "events.rounds", ct));
        Assert.Equal(oldPairs, await Snapshot(connection, "events.pairings", ct));
        await using (var countries = Command(connection, "select array_agg(code order by code) from profiles.countries"))
        { Assert.Equal(["BR", "US"], (string[])(await countries.ExecuteScalarAsync(ct))!); }
        await Execute(connection, "update events.rounds set algorithm_version=3 where id=$1", ct, seed.Rounds[1]);
        var low = seed.Alice.CompareTo(seed.Bob) < 0 ? seed.Alice : seed.Bob;
        var high = low == seed.Alice ? seed.Bob : seed.Alice;
        var mismatch = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, """
            insert into events.pairings(id,round_id,event_id,user_low,user_high,algorithm_version,communication_language,communication_policy_version,created_at)
            values(gen_random_uuid(),$1,$2,$3,$4,2,'en',1,now())
            """, ct, seed.Rounds[1], seed.Events[1], low, high));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, mismatch.SqlState);
        Assert.Equal("fk_pairings_round_algorithm", mismatch.ConstraintName);
        var unknown = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection,
            "update events.rounds set algorithm_version=4 where id=$1", ct, seed.Rounds[1]));
        Assert.Equal(PostgresErrorCodes.CheckViolation, unknown.SqlState);
        Assert.NotNull(await service.RoomAsync((await service.StatusAsync(seed.Events[0], seed.Alice, ct))!.RoomId!.Value, seed.Alice, ct));
    }

    private static async Task<string> Snapshot(NpgsqlConnection connection, string table, CancellationToken ct)
    {
        await using var query = Command(connection, $"select coalesce(jsonb_agg(row_to_json(t) order by id)::text,'[]') from {table} t");
        return (string)(await query.ExecuteScalarAsync(ct))!;
    }
}
