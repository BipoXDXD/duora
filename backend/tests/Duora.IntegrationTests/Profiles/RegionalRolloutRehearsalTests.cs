using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

// CI-only rehearsal: catches partial expansion, inferred consent and destructive rollback.
public sealed class RegionalRolloutRehearsalTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ExpansionWithTenThousandProfilesTimesOutSafelyAndRetryPreservesData()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await postgres.CreateEmptyDatabaseAsync(ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync("20261021150000_OperationalRetention", cancellationToken: ct);
        }
        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await Execute(connection, """
            insert into identity.users
              (id,user_name,normalized_user_name,email,normalized_email,email_confirmed,security_stamp,
               concurrency_stamp,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count,status,created_at)
            select gen_random_uuid(), 'rehearsal-'||n||'@example.com', upper('rehearsal-'||n||'@example.com'),
              'rehearsal-'||n||'@example.com',upper('rehearsal-'||n||'@example.com'),true,'synthetic','synthetic',
              false,false,true,0,'Active','2026-10-01T00:00:00Z'::timestamptz
            from generate_series(1,10000) n;
            insert into profiles.profiles(user_id,region_code,version,updated_at)
              select id,'BR-SP',7,'2026-10-01T00:00:00Z' from identity.users;
            insert into profiles.preferences(user_id,allow_different_display_locales,version,updated_at)
              select id,true,3,'2026-10-01T00:00:00Z' from identity.users;
            insert into profiles.communication_languages(user_id,language_code) select id,'pt' from identity.users;
            """, ct);
        var original = await Snapshot(connection, ct);
        var bounded = new NpgsqlConnectionStringBuilder(database.MigratorConnectionString)
        {
            Options = "-c lock_timeout=250ms",
        };
        var timer = Stopwatch.StartNew();
        long lockWaitMilliseconds;
        await using (var blocker = await connection.BeginTransactionAsync(ct))
        {
            await Execute(connection, "lock table identity.users in access exclusive mode", ct);
            await using var blocked = PostgresFixture.CreateDbContext(bounded.ConnectionString);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.GetService<IMigrator>()
                .MigrateAsync("20261021160000_RegionalPreferences", cancellationToken: ct));
            var error = Assert.IsType<PostgresException>(failure.GetBaseException());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            lockWaitMilliseconds = timer.ElapsedMilliseconds;
            TestContext.Current.TestOutputHelper!.WriteLine($"regional-rehearsal rows=10000 lock-timeout-ms={lockWaitMilliseconds}");
            await blocker.RollbackAsync(ct);
        }
        Assert.Equal(original, await Snapshot(connection, ct));
        await using (var absent = new NpgsqlCommand("select to_regclass('profiles.regional_preferences')::text", connection))
        {
            Assert.Equal(DBNull.Value, await absent.ExecuteScalarAsync(ct));
        }
        timer.Restart();
        await using var expanded = PostgresFixture.CreateDbContext(database.MigratorConnectionString);
        await expanded.Database.MigrateAsync(ct);
        var expansionMilliseconds = timer.ElapsedMilliseconds;
        TestContext.Current.TestOutputHelper!.WriteLine($"regional-rehearsal rows=10000 expansion-ms={expansionMilliseconds}");
        Assert.Equal(original, await Snapshot(connection, ct));
        await using (var pending = new NpgsqlCommand("select count(*) from profiles.regional_preferences", connection))
        {
            Assert.Equal(0L, await pending.ExecuteScalarAsync(ct));
        }
        // Re-running expansion is idempotent. Reversal keeps schema/data and refuses destructive Down.
        await expanded.Database.MigrateAsync(ct);
        await Execute(connection, """
            insert into profiles.regional_preferences(user_id,mode,version,updated_at)
              select id,'countries',1,now() from identity.users;
            insert into profiles.regional_preference_countries(user_id,country_code) select id,'BR' from identity.users;
            """, ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => expanded.GetService<IMigrator>()
            .MigrateAsync("20261021150000_OperationalRetention", cancellationToken: ct));
        Assert.Equal(original, await Snapshot(connection, ct));
        await using var retained = new NpgsqlCommand("""
            select (select count(*) from profiles.regional_preferences),
              (select count(*) from profiles.regional_preference_countries),
              (select string_agg(code,',' order by code) from profiles.countries),
              (select count(*) from profiles.subdivisions)
            """, connection);
        await using var reader = await retained.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(10000L, reader.GetInt64(0));
        Assert.Equal(10000L, reader.GetInt64(1));
        Assert.Equal("BR,US", reader.GetString(2));
        Assert.Equal(78L, reader.GetInt64(3));
        // Preserve successful measurements in the CI job summary without changing workflow or product logging.
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            await File.AppendAllTextAsync(summary,
                $"\nRegional rehearsal: 10,000 synthetic profiles; lock timeout {lockWaitMilliseconds} ms; expansion {expansionMilliseconds} ms. Data and explicit choices preserved.\n", ct);
        }
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> Snapshot(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            select md5(string_agg(row_to_json(t)::text,',' order by id)) from (
              -- region_code é a cópia legada que o contract (#78) retira; a localização canônica continua na comparação.
              select u.*,to_jsonb(p)-'region_code' profile,row_to_json(l) preferences,c.language_code
              from identity.users u join profiles.profiles p on p.user_id=u.id
              join profiles.preferences l on l.user_id=u.id
              join profiles.communication_languages c on c.user_id=u.id
            ) t
            """, connection);
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }
}
