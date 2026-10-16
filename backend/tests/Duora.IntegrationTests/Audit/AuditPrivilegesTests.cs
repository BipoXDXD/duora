using Npgsql;

namespace Duora.IntegrationTests.Audit;

/// <summary>
/// `ops.audit_events` é append-only por GRANT (D3, TM-05). A allowlist (`AppRoleTableAccess`, entrada `AppendOnly`)
/// confere os privilégios no catálogo; aqui o mesmo resultado é provado pelo comportamento, conectado como
/// <c>duora_app</c> no banco da suíte, migrado como em produção.
/// </summary>
public sealed class AuditPrivilegesTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AppRoleCanInsertAndSelectAuditEvents()
    {
        var targetId = Guid.CreateVersion7();
        await using var app = await OpenAsAppAsync();

        await using (var insert = new NpgsqlCommand(
            """
            insert into ops.audit_events (occurred_at, actor_kind, action, target_type, target_id)
            values (now(), 'System', 'safety.block_created', 'user', $1)
            """,
            app))
        {
            insert.Parameters.AddWithValue(targetId);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await using var count = new NpgsqlCommand("select count(*) from ops.audit_events where target_id = $1", app);
        count.Parameters.AddWithValue(targetId);
        Assert.Equal(1L, await count.ExecuteScalarAsync(Ct));
    }

    [Theory]
    [InlineData("update ops.audit_events set action = 'safety.block_lifted'")]
    [InlineData("delete from ops.audit_events")]
    [InlineData("truncate ops.audit_events")]
    public async Task AppRoleCannotRewriteTheAuditTrail(string sql)
    {
        await using var app = await OpenAsAppAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, sql));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private async Task<NpgsqlConnection> OpenAsAppAsync()
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        Assert.Equal("duora_app", connection.UserName);
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
