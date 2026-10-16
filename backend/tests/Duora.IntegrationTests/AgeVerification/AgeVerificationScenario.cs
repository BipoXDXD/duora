using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Duora.Infrastructure.Identity;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>Linha de <c>identity.age_verifications</c> como o teste a lê.</summary>
internal sealed record VerificationRow(
    Guid Id,
    string State,
    string PolicyVersion,
    string Provider,
    string? ProviderReference,
    string RequestNonce,
    string? FlowUrl,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? ExpiresAt,
    string? DecisionSource,
    long Version);

/// <summary>
/// Passos da verificação etária pela API, sobre uma conta confirmada (estado <c>PendingEligibility</c>), com o
/// fornecedor simulado. Lê o banco diretamente para afirmar o efeito, como os demais testes de identidade.
/// </summary>
internal sealed class AgeVerificationScenario(PostgresFixture postgres, DuoraWebFactory factory)
{
    public const string RequestsPath = "/api/v1/me/age-verifications";
    public const string CurrentPath = "/api/v1/me/age-verifications/current";
    public const string FakeFlowOrigin = "https://age-check.fake.invalid";

    /// <summary>Segredos de assinatura do fornecedor simulado, sorteados por execução (nada fixo no repositório).</summary>
    public static readonly string CurrentSigningSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static readonly string NextSigningSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Configuração do fornecedor simulado que os testes usam; segredos sintéticos, só de teste.</summary>
    public static Dictionary<string, string?> FakeProviderSettings(string mode = "Succeed") => new()
    {
        ["AgeVerification:Fake:SigningSecrets:0"] = CurrentSigningSecret,
        ["AgeVerification:Fake:SigningSecrets:1"] = NextSigningSecret,
        ["AgeVerification:Provider"] = "Fake",
        ["AgeVerification:PolicyVersion"] = "test-2026-10",
        ["AgeVerification:AllowedFlowOrigins:0"] = FakeFlowOrigin,
        ["AgeVerification:Fake:Mode"] = mode,
        ["AgeVerification:Fake:FlowBaseUrl"] = FakeFlowOrigin + "/flow",
    };

    public IdentityScenario Identity => new(postgres, factory);

    public async Task<(TestAccount Account, Browser Browser)> SignedInPendingAccountAsync()
    {
        var account = await Identity.CreateConfirmedAccountAsync();
        return (account, await Identity.SignedInBrowserAsync(account));
    }

    public static Task<HttpResponseMessage> RequestAsync(Browser browser) => browser.PostRawJsonAsync(RequestsPath, "{}");

    public static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException("Corpo vazio.");
    }

    public static async Task<JsonObject> CurrentAsync(Browser browser)
    {
        using var response = await browser.GetAsync(CurrentPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    public async Task<IReadOnlyList<VerificationRow>> VerificationsAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return await ReadVerificationsAsync(connection, userId);
    }

    public static async Task<IReadOnlyList<VerificationRow>> ReadVerificationsAsync(NpgsqlConnection connection, Guid userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, state, policy_version, provider, provider_reference, request_nonce, provider_flow_url,
                   requested_at, decided_at, expires_at, decision_source, version
            from identity.age_verifications where user_id = $1
            order by requested_at, id
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        var rows = new List<VerificationRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new VerificationRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt64(11)));
        }

        return rows;
    }

    public async Task<string> AccountStatusAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select status from identity.users where id = $1", connection);
        command.Parameters.AddWithValue(userId);
        return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Conta inexistente.");
    }

    /// <summary>Eventos da outbox que pedem ao worker a abertura no fornecedor, para as solicitações da pessoa.</summary>
    public Task<long> CountProviderTasksAsync(Guid userId) =>
        Identity.ScalarAsync(
            """
            select count(*) from ops.outbox_events e
            join identity.age_verifications v on v.id = e.resource_id
            where e.type = 'identity.age-verification-requested' and v.user_id = $1
            """,
            userId);

    public Task<long> CountAuditAsync(string action, Guid actorOrTarget) =>
        Identity.ScalarAsync(
            "select count(*) from ops.audit_events where action = $1 and (actor_id = $2 or target_id = $2)",
            action,
            actorOrTarget);

    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Simula o worker: grava a referência e o fluxo do fornecedor na solicitação aberta.</summary>
    public Task MarkProviderFlowReadyAsync(Guid verificationId, string flowUrl) =>
        ExecuteAsync(
            "update identity.age_verifications set provider_reference = $2, provider_flow_url = $3, version = version + 1 where id = $1",
            verificationId,
            $"{FakeAgeEligibilityProvider.ReferenceFor(verificationId)}",
            flowUrl);

    /// <summary>
    /// Estado de uma conta que já passou pela verificação, gravado direto como o webhook o grava: sem
    /// <c>expires_at</c>, porque a verificação adulta não vence.
    /// </summary>
    public async Task MakeVerifiedAdultAsync(Guid userId, DateTimeOffset decidedAt)
    {
        await ExecuteAsync(
            """
            insert into identity.age_verifications
                (id, user_id, state, policy_version, provider, provider_reference, request_nonce, requested_at,
                 decided_at, decision_source, version)
            values (gen_random_uuid(), $1, 'VerifiedAdult', 'test-2026-10', 'fake', 'ref-' || gen_random_uuid(),
                    'nonce-' || gen_random_uuid(), $2, $2, 'Callback', 1)
            """,
            userId,
            decidedAt);
        await ExecuteAsync("update identity.users set status = 'Active' where id = $1", userId);
    }

    /// <summary>Solicitação já decidida (<c>Rejected</c>, <c>ManualReview</c>...), sem mexer na conta.</summary>
    public Task AddDecidedVerificationAsync(Guid userId, string state, DateTimeOffset decidedAt, Guid? id = null) =>
        ExecuteAsync(
            """
            insert into identity.age_verifications
                (id, user_id, state, policy_version, provider, provider_reference, request_nonce, requested_at,
                 decided_at, decision_source, version)
            values ($4, $1, $2, 'test-2026-10', 'fake', 'ref-' || gen_random_uuid(),
                    'nonce-' || gen_random_uuid(), $3, $3, 'Callback', 1)
            """,
            userId,
            state,
            decidedAt,
            id ?? Guid.NewGuid());

    /// <summary>Solicitação sem decisão (<c>Pending</c> ou <c>Expired</c>) e sem fluxo do fornecedor.</summary>
    public Task AddUndecidedRequestAsync(Guid userId, string state, DateTimeOffset requestedAt) =>
        ExecuteAsync(
            """
            insert into identity.age_verifications
                (id, user_id, state, policy_version, provider, request_nonce, requested_at, version)
            values (gen_random_uuid(), $1, $2, 'test-2026-10', 'fake', 'nonce-' || gen_random_uuid(), $3, 0)
            """,
            userId,
            state,
            requestedAt);

    /// <summary>Encerra a tarefa do worker da solicitação aberta como se ele tivesse desistido ou o prazo tivesse vencido.</summary>
    public Task SetProviderTaskStatusAsync(Guid userId, string status) =>
        ExecuteAsync(
            """
            update ops.outbox_deliveries d set status = $2, lease_expires_at = null, completed_at = now()
            from ops.outbox_events e join identity.age_verifications v on v.id = e.resource_id
            where d.outbox_event_id = e.id and v.user_id = $1 and v.state = 'Pending'
            """,
            userId,
            status);
}
