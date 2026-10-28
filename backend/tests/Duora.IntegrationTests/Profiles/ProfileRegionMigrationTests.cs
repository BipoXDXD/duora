using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

/// <summary>
/// A migration ProfileRegion é aditiva: o perfil gravado antes dela continua igual, com a região "não informada"
/// (<c>null</c>) e a mesma versão, para o ETag que o cliente já tem continuar valendo.
/// </summary>
public sealed class ProfileRegionMigrationTests(PostgresFixture postgres)
{
    private const string MigrationBeforeRegion = "20261016040228_WebhookReceipts";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExistingProfileKeepsItsTextAndVersionAndHasNoRegion()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await before.GetService<IMigrator>().MigrateAsync(MigrationBeforeRegion, cancellationToken: Ct);
        }

        Guid userId;
        await using (var connection = new NpgsqlConnection(database.AppConnectionString))
        {
            await connection.OpenAsync(Ct);
            userId = await RoleSeparationTests.InsertUserAsync(connection);
            await using var insert = new NpgsqlCommand(
                """
                insert into profiles.profiles (user_id, display_name, bio, version, updated_at)
                values ($1, 'Ana', 'Oi', 3, now())
                """,
                connection);
            insert.Parameters.AddWithValue(userId);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(Ct));
        }

        await using (var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            // Até o schema expandido: o contract (#78) retira region_code, verificado em ProfileRegionContractMigrationTests.
            await after.GetService<IMigrator>().MigrateAsync(
                ProfileRegionContractMigrationTests.MigrationBeforeContract, cancellationToken: Ct);
        }

        await using var check = new NpgsqlConnection(database.AppConnectionString);
        await check.OpenAsync(Ct);
        await using var read = new NpgsqlCommand(
            """
            select display_name || '|' || bio || '|' || coalesce(region_code, '<null>') || '|' || version
            from profiles.profiles where user_id = $1
            """,
            check);
        read.Parameters.AddWithValue(userId);
        Assert.Equal("Ana|Oi|<null>|3", await read.ExecuteScalarAsync(Ct));
    }
}
