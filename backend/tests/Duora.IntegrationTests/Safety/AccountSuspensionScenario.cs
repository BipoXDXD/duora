using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>Participante elegível, moderador com MFA e as rotas de moderação de conta (issue #299), pela API.</summary>
internal sealed class AccountSuspensionScenario(string connectionString, DuoraWebFactory factory, FakeTimeProvider time)
{
    public const string ParticipationSentinel = "/api/v1/test/eligible-participant";

    public IdentityScenario Identity { get; } = new(connectionString, factory);

    public static string StatePath(Guid userId) => $"/api/v1/admin/accounts/{userId}/suspension";

    public static string SuspendPath(Guid userId) => $"/api/v1/admin/accounts/{userId}/suspensions";

    public static string LiftPath(Guid userId, Guid suspensionId) =>
        $"/api/v1/admin/accounts/{userId}/suspensions/{suspensionId}/lift";

    public static string NewKey() => $"k-{Guid.NewGuid():N}";

    public static object SuspendBody(string internalReason = "Ofensas repetidas contra os Termos.") =>
        new { reasonCode = "TermsViolation", internalReason };

    public static MutationHeaders WithKey(string key) =>
        new(Extra: new Dictionary<string, string> { ["Idempotency-Key"] = key });

    public static MutationHeaders WithIfMatch(string eTag) =>
        new(Extra: new Dictionary<string, string> { ["If-Match"] = eTag });

    /// <summary>Conta ativa, com e-mail confirmado e selfie aprovada: alcança a rota sentinela de participação.</summary>
    public async Task<TestAccount> CreateEligibleParticipantAsync()
    {
        var account = await Identity.CreateConfirmedAccountAsync();
        await ExecuteAsync(
            """
            insert into identity.selfie_verifications (id, user_id, state, policy_version, requested_at, expires_at, version)
            values (gen_random_uuid(), $1, 'Passed', 'test', now(), now(), 0)
            """,
            account.UserId);
        await ExecuteAsync("update identity.users set status = 'Active' where id = $1", account.UserId);
        return account;
    }

    /// <summary>Conta privilegiada ativa com TOTP ligado e sessão administrativa com o TOTP recém-provado.</summary>
    public async Task<(TestAccount Account, Browser Browser)> PrivilegedBrowserAsync(string role = AccountRoles.Moderator)
    {
        var account = await Identity.CreatePrivilegedAccountAsync(role);
        await ExecuteAsync("update identity.users set status = 'Active' where id = $1", account.UserId);
        var browser = await Identity.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, time);
        return (account, browser);
    }

    public static async Task<JsonObject> SuspendAsync(Browser moderator, Guid userId, string? key = null)
    {
        using var response = await moderator.PostJsonAsync(SuspendPath(userId), SuspendBody(), WithKey(key ?? NewKey()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await MfaScenario.ReadObjectAsync(response);
    }

    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public Task<long> CountAsync(string sql, params object[] parameters) => Identity.ScalarAsync(sql, parameters);

    public Task<string> TextAsync(string sql, params object[] parameters) => Identity.ScalarTextAsync(sql, parameters);
}
