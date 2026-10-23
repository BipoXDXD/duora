using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.IntegrationTests.Database;

public sealed class CanonicalReconciliationTests(PostgresFixture postgres)
{
    private static readonly string[] RewrittenPrivileges = ["UPDATE", "DELETE", "TRUNCATE"];

    [Fact]
    public async Task CanonicalSqlRemovesDeliberatelyGrantedPrivilegesWithoutChangingHistoryOrLegitimateAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await postgres.CreateEmptyDatabaseAsync(ct);
        await using (var context = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await context.Database.MigrateAsync(ct);
        }

        await using var owner = new NpgsqlConnection(database.MigratorConnectionString);
        await owner.OpenAsync(ct);
        var historyBefore = await HistoryAsync(owner, ct);
        Assert.NotEmpty(historyBefore);
        var sentinel = Guid.CreateVersion7();
        await InsertAuditAsync(owner, sentinel, ct);

        // A compiled allowlist, never participant input. Include the regional TRUNCATE exception explicitly.
        var protectedTables = AppRoleTableAccess.Expected.Where(entry => entry.Value != AppTableAccess.ReadWrite ||
            entry.Key is "profiles.regional_preferences" or "profiles.regional_preference_countries").ToArray();
        var forbidden = protectedTables.SelectMany(entry => RewrittenPrivileges
            .Except(AppRoleTableAccess.PrivilegesOf(entry.Value))
            .Select(privilege => (Table: entry.Key, Privilege: privilege))).ToArray();
        Assert.NotEmpty(forbidden);
        foreach (var item in forbidden)
        {
            await using var grant = new NpgsqlCommand($"grant {item.Privilege} on table {item.Table} to duora_app", owner);
            await grant.ExecuteNonQueryAsync(ct);
            Assert.True(await HasPrivilegeAsync(owner, item.Table, item.Privilege, ct),
                $"Precondition: {item.Table}:{item.Privilege} was not actually granted");
        }

        // Migrations already revoke ACL: only a reconciliation AFTER the verified grants can satisfy this proof.
        await postgres.ReconcilePrivilegesAsync(database.Name, ct);

        foreach (var item in forbidden)
        {
            Assert.False(await HasPrivilegeAsync(owner, item.Table, item.Privilege, ct),
                $"Canonical payload left {item.Table}:{item.Privilege}");
        }
        Assert.Equal(historyBefore, await HistoryAsync(owner, ct));

        await using var app = new NpgsqlConnection(database.AppConnectionString);
        await app.OpenAsync(ct);
        await using (var count = new NpgsqlCommand("select count(*) from ops.audit_events where target_id = $1", app))
        {
            count.Parameters.AddWithValue(sentinel);
            Assert.Equal(1L, await count.ExecuteScalarAsync(ct));
        }
        await InsertAuditAsync(app, Guid.CreateVersion7(), ct);
        foreach (var sql in new[] { "update ops.audit_events set action = 'safety.block_lifted'",
                     "delete from ops.audit_events", "truncate ops.audit_events" })
        {
            await using var transaction = await app.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand(sql, app, transaction);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            await transaction.RollbackAsync(ct);
        }
    }

    private static async Task<bool> HasPrivilegeAsync(NpgsqlConnection owner, string table, string privilege,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("select has_table_privilege('duora_app', $1, $2)", owner);
        command.Parameters.AddWithValue(table);
        command.Parameters.AddWithValue(privilege);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<string> HistoryAsync(NpgsqlConnection owner, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "select coalesce(string_agg(\"MigrationId\" || ':' || \"ProductVersion\", ',' order by \"MigrationId\"), '') " +
            "from ops.__ef_migrations_history", owner);
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task InsertAuditAsync(NpgsqlConnection connection, Guid target, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "insert into ops.audit_events (occurred_at, actor_kind, action, target_type, target_id) " +
            "values (now(), 'System', 'safety.block_created', 'user', $1)", connection);
        command.Parameters.AddWithValue(target);
        await command.ExecuteNonQueryAsync(ct);
    }
}
