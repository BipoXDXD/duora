using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>Leitura do estado de segurança de um par, pela conexão do teste, para afirmar o efeito no banco.</summary>
internal sealed record BlockState(long Rows, long Audits, long BlockerVersion, long BlockedVersion);

/// <summary>
/// Contas sintéticas prontas para bloquear. O M1 não tem caminho até <c>Active</c> (verificação etária vem
/// depois), então o estado é posto direto no banco, como pede a decisão G4.
/// </summary>
internal sealed class SafetyScenario(PostgresFixture postgres, DuoraWebFactory factory)
{
    private readonly IdentityScenario _identity = new(postgres, factory);

    public static string BlockPath(Guid targetUserId) => $"/api/v1/me/blocks/{targetUserId}";

    public async Task<TestAccount> CreateAccountAsync(string status = "Active")
    {
        var account = await _identity.CreateConfirmedAccountAsync();
        await SetStatusAsync(account.UserId, status);
        return account;
    }

    public Task<Browser> SignedInBrowserAsync(TestAccount account) => _identity.SignedInBrowserAsync(account);

    public async Task SetStatusAsync(Guid userId, string status)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("update identity.users set status = $1 where id = $2", connection);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(userId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Linhas de bloqueio blocker → blocked, auditorias do bloqueio e versão de segurança de cada guarda.</summary>
    public async Task<BlockState> ReadAsync(Guid blockerId, Guid blockedId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select
              (select count(*) from safety.blocks where blocker_id = $1 and blocked_id = $2),
              (select count(*) from ops.audit_events
                where action = 'safety.block_created' and actor_id = $1 and target_id = $2),
              coalesce((select security_version from safety.user_guards where user_id = $1), -1),
              coalesce((select security_version from safety.user_guards where user_id = $2), -1)
            """,
            connection);
        command.Parameters.AddWithValue(blockerId);
        command.Parameters.AddWithValue(blockedId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new BlockState(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    /// <summary>Tudo o que um pedido de bloqueio de <paramref name="userId"/> poderia ter deixado no banco.</summary>
    public async Task<long> CountTracesOfAsync(Guid userId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select (select count(*) from safety.blocks where blocker_id = $1 or blocked_id = $1)
                 + (select count(*) from ops.audit_events where actor_id = $1 or target_id = $1)
                 + (select count(*) from safety.user_guards where user_id = $1 and security_version > 0)
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? 0L);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}
