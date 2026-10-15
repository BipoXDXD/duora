using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>G7: a migration que cria `safety.user_guards` dá uma guarda a cada conta que já existia.</summary>
public sealed class UserGuardsMigrationTests(PostgresFixture postgres)
{
    private const string MigrationBeforeSafety = "20261015231135_AuditEvents";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExistingAccountsGetAGuardWithVersionZero()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var before = PostgresFixture.CreateDbContext(database))
        {
            await before.GetService<IMigrator>().MigrateAsync(MigrationBeforeSafety, cancellationToken: Ct);
        }

        var existing = Guid.CreateVersion7();
        await using (var connection = new NpgsqlConnection(database))
        {
            await connection.OpenAsync(Ct);
            await using var insert = new NpgsqlCommand(
                """
                insert into identity.users (id, status, created_at, email_confirmed, phone_number_confirmed,
                                            two_factor_enabled, lockout_enabled, access_failed_count)
                values ($1, 'PendingEligibility', now(), true, false, false, true, 0)
                """,
                connection);
            insert.Parameters.AddWithValue(existing);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await using (var after = PostgresFixture.CreateDbContext(database))
        {
            await after.Database.MigrateAsync(Ct);
        }

        await using var check = new NpgsqlConnection(database);
        await check.OpenAsync(Ct);
        await using var read = new NpgsqlCommand(
            "select security_version from safety.user_guards where user_id = $1", check);
        read.Parameters.AddWithValue(existing);
        Assert.Equal(0L, await read.ExecuteScalarAsync(Ct));
    }
}
