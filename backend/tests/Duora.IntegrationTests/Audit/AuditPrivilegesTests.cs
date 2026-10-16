using Npgsql;

namespace Duora.IntegrationTests.Audit;

/// <summary>
/// `ops.audit_events` é append-only por GRANT (D3, TM-05): com os papéis de produção, o app só insere e lê.
/// Diferente de <c>scripts/deploy/test_db_privileges.py</c>, que simula a migration, este usa a migration real do
/// EF aplicada pelo fixture como <c>duora_migrator</c> e tenta alterar o histórico como <c>duora_app</c>.
/// </summary>
public sealed class AuditPrivilegesTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AppRoleCanOnlyInsertAndSelectAuditEvents()
    {
        await using var app = await OpenFreshDatabaseAsAppAsync();

        await ExecuteAsync(
            app,
            """
            insert into ops.audit_events (occurred_at, actor_kind, action, target_type, target_id)
            values (now(), 'System', 'safety.block_created', 'user', gen_random_uuid())
            """);
        await using (var count = new NpgsqlCommand("select count(*) from ops.audit_events", app))
        {
            Assert.Equal(1L, await count.ExecuteScalarAsync(Ct));
        }

        foreach (var sql in new[]
                 {
                     "update ops.audit_events set action = 'safety.block_lifted'",
                     "delete from ops.audit_events",
                     "truncate ops.audit_events",
                 })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task AppRoleKeepsFullDmlOnOtherTables()
    {
        await using var app = await OpenFreshDatabaseAsAppAsync();

        // Prova que o REVOKE é só da tabela append-only: a outbox continua com DML completo.
        await ExecuteAsync(
            app,
            """
            insert into ops.outbox_events (id, type, schema_version, resource_type, resource_id, payload, occurred_at)
            values (gen_random_uuid(), 'test', 1, 'user', gen_random_uuid(), '{}', now())
            """);
        await ExecuteAsync(app, "update ops.outbox_events set payload = '{}'");
        await ExecuteAsync(app, "delete from ops.outbox_events");
    }

    /// <summary>
    /// Banco novo migrado pelo fixture como <c>duora_migrator</c> e aberto como <c>duora_app</c>, os papéis de
    /// produção (PR #26).
    /// </summary>
    private async Task<NpgsqlConnection> OpenFreshDatabaseAsAppAsync()
    {
        var connection = new NpgsqlConnection(await postgres.CreateMigratedDatabaseAsync(Ct));
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
