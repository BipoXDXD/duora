using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

public sealed class ProfileLocationMigrationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A ponte de expand continua valendo no schema expandido, que o Down do contract (#78) devolve.</summary>
    [Fact]
    public async Task LegacyBinaryWritesStayReadableAndDoNotResurrectClearedRegions()
    {
        var database = await ProfileRegionContractMigrationTests.MigrateToAsync(
            postgres, ProfileRegionContractMigrationTests.MigrationBeforeContract);
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(Ct);
        var id = await RoleSeparationTests.InsertUserAsync(connection);
        await using var command = new NpgsqlCommand("""
            insert into profiles.profiles (user_id, region_code, version, updated_at)
            values ($1, 'BR-SP', 1, now());
            """, connection);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync(Ct);
        command.CommandText = "select country_code || '|' || subdivision_code from profiles.profiles where user_id = $1";
        Assert.Equal("BR|BR-SP", await command.ExecuteScalarAsync(Ct));
        command.CommandText = "update profiles.profiles set region_code = 'BR-BA', version = version + 1 where user_id = $1 returning subdivision_code";
        Assert.Equal("BR-BA", await command.ExecuteScalarAsync(Ct));
        command.CommandText = "update profiles.profiles set region_code = null, version = version + 1 where user_id = $1 returning country_code";
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync(Ct));
        command.CommandText = "update profiles.profiles set country_code = 'BR', subdivision_code = null, region_code = null, version = version + 1 where user_id = $1 returning country_code";
        Assert.Equal("BR", await command.ExecuteScalarAsync(Ct));
        // The old binary includes region_code in its SET even when editing only bio.
        command.CommandText = "update profiles.profiles set bio = 'Hello', region_code = region_code, version = version + 1 where user_id = $1 returning country_code";
        Assert.Equal("BR", await command.ExecuteScalarAsync(Ct));
        command.CommandText = "update profiles.profiles set country_code = null, subdivision_code = null, region_code = null, version = version + 1 where user_id = $1 returning country_code";
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task BackfillPreservesBrazilianRegionsNullsAndVersions()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync("20261019150000_Participation", cancellationToken: Ct);
        }

        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var region in Duora.Core.Profiles.RegionCode.All.Cast<string?>().Append(null))
        {
            var id = await RoleSeparationTests.InsertUserAsync(connection);
            await using var insert = new NpgsqlCommand("insert into profiles.profiles (user_id, region_code, version, updated_at) values ($1, $2, 7, '2026-01-01Z')", connection);
            insert.Parameters.AddWithValue(id);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)region ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await using (var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            // Para antes do contract (#78), que retira region_code: aqui se compara o backfill com a coluna antiga.
            await after.GetService<IMigrator>().MigrateAsync(
                ProfileRegionContractMigrationTests.MigrationBeforeContract, cancellationToken: Ct);
        }

        await using var check = new NpgsqlCommand("""
            select count(*) from profiles.profiles
            where subdivision_code is not distinct from region_code
              and country_code is not distinct from (case when region_code is null then null else 'BR' end)
              and version = 7 and updated_at = '2026-01-01Z'
            """, connection);
        Assert.Equal(28L, await check.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task ForeignKeysAllowCountryOnlyAndRejectInvalidPairsAndUseIndex()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var context = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await context.Database.MigrateAsync(Ct);
        }

        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var (country, subdivision, sqlState) in new (string?, string?, string?)[]
        {
            ("US", "BR-SP", PostgresErrorCodes.ForeignKeyViolation),
            ("BR", "US-MA", PostgresErrorCodes.ForeignKeyViolation),
            ("US", "US-MA", null),
            ("US", null, null),
            (null, "BR-SP", PostgresErrorCodes.CheckViolation),
            ("ZZ", null, PostgresErrorCodes.ForeignKeyViolation),
            ("BR", "BR-XX", PostgresErrorCodes.ForeignKeyViolation),
            ("BR", null, null),
            (null, null, null),
        })
        {
            var id = await RoleSeparationTests.InsertUserAsync(connection);
            await using var insert = new NpgsqlCommand("insert into profiles.profiles (user_id, country_code, subdivision_code, version, updated_at) values ($1, $2, $3, 1, now())", connection);
            insert.Parameters.AddWithValue(id);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)country ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)subdivision ?? DBNull.Value });
            if (sqlState is null)
            {
                Assert.Equal(1, await insert.ExecuteNonQueryAsync(Ct));
            }
            else
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));
                Assert.Equal(sqlState, error.SqlState);
            }
        }
        await using var explain = new NpgsqlCommand("set enable_seqscan = off; explain select user_id from profiles.profiles where country_code = 'BR'", connection);
        await using var reader = await explain.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }
        Assert.Contains("ix_profiles_location", string.Join('\n', lines), StringComparison.Ordinal);
    }
}
