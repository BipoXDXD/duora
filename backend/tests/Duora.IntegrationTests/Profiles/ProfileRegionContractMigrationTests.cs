using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;

namespace Duora.IntegrationTests.Profiles;

/// <summary>
/// Contract da issue #78: a coluna legada <c>region_code</c>, o CHECK das 27 UFs e o trigger de ponte saem do schema.
/// A localização canônica (país + subdivisão), a versão e o <c>updated_at</c> não mudam, e o <c>Down</c> devolve a
/// ponte legada derivada da localização canônica, sem perder dado.
/// </summary>
public sealed class ProfileRegionContractMigrationTests(PostgresFixture postgres)
{
    /// <summary>Última migration antes do contract: o schema expandido, com a ponte legada.</summary>
    public const string MigrationBeforeContract = "20261025120000_PhotoReviewAppealDecisions";

    public const string ContractMigration = "20261028074839_ProfileRegionContract";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ContractRemovesTheLegacyColumnCheckAndTrigger()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var context = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await context.Database.MigrateAsync(Ct);
        }

        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        Assert.Equal("0|0|0|0", await LegacyObjectsAsync(connection));
    }

    [Fact]
    public async Task ContractKeepsCanonicalLocationVersionAndTimestamp()
    {
        var database = await MigrateToAsync(MigrationBeforeContract);
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(Ct);
        var locations = Duora.Core.Profiles.RegionCode.All
            .Select(code => ((string?)"BR", (string?)code))
            .Append(("BR", null))
            .Append(("US", "US-MA"))
            .Append(("US", null))
            .Append((null, null))
            .ToList();
        foreach (var (country, subdivision) in locations)
        {
            await InsertCanonicalAsync(connection, country, subdivision);
        }
        var before = await CanonicalSnapshotAsync(connection);

        await using (var context = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(ContractMigration, cancellationToken: Ct);
        }

        Assert.Equal(before, await CanonicalSnapshotAsync(connection));
        await using var count = new NpgsqlCommand("select count(*) from profiles.profiles", connection);
        Assert.Equal((long)locations.Count, await count.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task DownRestoresTheLegacyBridgeDerivedFromTheCanonicalLocation()
    {
        var database = await MigrateToAsync(ContractMigration);
        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        var brazil = await InsertCanonicalAsync(connection, "BR", "BR-SP");
        var countryOnly = await InsertCanonicalAsync(connection, "BR", null);
        var unitedStates = await InsertCanonicalAsync(connection, "US", "US-MA");
        var empty = await InsertCanonicalAsync(connection, null, null);
        var before = await CanonicalSnapshotAsync(connection);

        await using (var context = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(MigrationBeforeContract, cancellationToken: Ct);
        }

        Assert.Equal("1|1|1|1", await LegacyObjectsAsync(connection));
        Assert.Equal(before, await CanonicalSnapshotAsync(connection));
        Assert.Equal("BR-SP", await LegacyRegionAsync(connection, brazil));
        Assert.Equal(DBNull.Value, await LegacyRegionAsync(connection, countryOnly));
        Assert.Equal(DBNull.Value, await LegacyRegionAsync(connection, unitedStates));
        Assert.Equal(DBNull.Value, await LegacyRegionAsync(connection, empty));

        // O trigger devolvido volta a traduzir a escrita de um binário antigo para a localização canônica.
        await using var legacyWrite = new NpgsqlCommand(
            "update profiles.profiles set region_code = 'BR-BA' where user_id = $1 returning country_code || '|' || subdivision_code",
            connection);
        legacyWrite.Parameters.AddWithValue(empty);
        Assert.Equal("BR|BR-BA", await legacyWrite.ExecuteScalarAsync(Ct));

        // E o CHECK devolvido volta a recusar código fora das 27 UFs.
        await using var invalid = new NpgsqlCommand(
            "update profiles.profiles set region_code = 'US-MA' where user_id = $1", connection);
        invalid.Parameters.AddWithValue(brazil);
        var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_profiles_region_code", error.ConstraintName);
    }

    [Fact]
    public async Task ContractCanBeReappliedAfterDown()
    {
        var database = await MigrateToAsync(ContractMigration);
        await using (var context = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(MigrationBeforeContract, cancellationToken: Ct);
            await migrator.MigrateAsync(ContractMigration, cancellationToken: Ct);
        }

        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        Assert.Equal("0|0|0|0", await LegacyObjectsAsync(connection));
    }

    /// <summary>Cria um banco próprio migrado até <paramref name="migration"/>; nunca usa o banco compartilhado.</summary>
    internal static async Task<ScratchDatabase> MigrateToAsync(PostgresFixture postgres, string migration)
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using var context = PostgresFixture.CreateDbContext(database.MigratorConnectionString);
        await context.GetService<IMigrator>().MigrateAsync(migration, cancellationToken: Ct);
        return database;
    }

    private Task<ScratchDatabase> MigrateToAsync(string migration) => MigrateToAsync(postgres, migration);

    /// <summary>"coluna|CHECK|trigger|função" do contrato legado, cada um contado no catálogo.</summary>
    private static async Task<string> LegacyObjectsAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            select
              (select count(*) from pg_catalog.pg_attribute
                where attrelid = 'profiles.profiles'::regclass and attname = 'region_code' and attnum > 0 and not attisdropped)
              || '|' ||
              (select count(*) from pg_catalog.pg_constraint
                where conrelid = 'profiles.profiles'::regclass and conname = 'ck_profiles_region_code')
              || '|' ||
              (select count(*) from pg_catalog.pg_trigger
                where tgrelid = 'profiles.profiles'::regclass and tgname = 'sync_legacy_region')
              || '|' ||
              (select count(*) from pg_catalog.pg_proc p join pg_catalog.pg_namespace n on n.oid = p.pronamespace
                where n.nspname = 'profiles' and p.proname = 'sync_legacy_region')
            """, connection);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<Guid> InsertCanonicalAsync(NpgsqlConnection connection, string? country, string? subdivision)
    {
        var id = await RoleSeparationTests.InsertUserAsync(connection);
        await using var insert = new NpgsqlCommand("""
            insert into profiles.profiles (user_id, country_code, subdivision_code, version, updated_at)
            values ($1, $2, $3, 7, '2026-01-01Z')
            """, connection);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)country ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)subdivision ?? DBNull.Value });
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(Ct));
        return id;
    }

    private static async Task<string> CanonicalSnapshotAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            select string_agg(
              user_id || '|' || coalesce(country_code, '<null>') || '|' || coalesce(subdivision_code, '<null>')
                || '|' || version || '|' || updated_at,
              ',' order by user_id)
            from profiles.profiles
            """, connection);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<object?> LegacyRegionAsync(NpgsqlConnection connection, Guid user)
    {
        await using var command = new NpgsqlCommand("select region_code from profiles.profiles where user_id = $1", connection);
        command.Parameters.AddWithValue(user);
        return await command.ExecuteScalarAsync(Ct);
    }
}
