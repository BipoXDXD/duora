using System.Net;
using System.Text.Json;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>Conta criada e confirmada pela API, pronta para login.</summary>
internal sealed record TestAccount(Guid UserId, string Email, string Password);

/// <summary>Link de conta que a aplicação mandaria por e-mail, lido da outbox.</summary>
internal sealed record AccountLink(Guid UserId, string Token);

/// <summary>
/// Passos de conta pela API, com dados sintéticos únicos por teste (o banco é compartilhado pela suíte). Os links
/// de e-mail são lidos do payload da outbox, que o teste HTTP não despacha: é o mesmo dado que o worker usaria.
/// </summary>
internal sealed class IdentityScenario(string connectionString, DuoraWebFactory factory)
{
    public IdentityScenario(PostgresFixture postgres, DuoraWebFactory factory)
        : this(postgres.ConnectionString, factory)
    {
    }

    public async Task<AccountLink> CreateLegacyConfirmationProofAsync(string email)
    {
        var proof = await ReadLatestLinkAsync(email, "EmailConfirmation");
        await ScalarAsync("delete from identity.user_tokens where user_id = $1 and login_provider = 'Duora.EmailConfirmation' returning 1", proof.UserId);
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(factory.Services);
        var users = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Duora.Infrastructure.Identity.DuoraUser>>(scope.ServiceProvider);
        var user = await users.FindByIdAsync(proof.UserId.ToString("D"));
        Assert.NotNull(user);
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        return new AccountLink(proof.UserId, System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(token)));
    }

    public const string ValidPassword = "frase longa o bastante";

    public const string TermsVersion = "2026-10-01";

    public static string NewEmail() => $"pessoa-{Guid.NewGuid():N}@example.com";

    public static object RegistrationBody(string email, string password = ValidPassword,
        Duora.Core.Identity.LegalPublication? publication = null, string termsLocale = "pt-BR", string privacyLocale = "pt-BR") => new
    {
        email,
        password,
        acceptedTermsVersion = (publication ?? SyntheticLegalPublication.Default).TermsVersion,
        acceptedPrivacyVersion = (publication ?? SyntheticLegalPublication.Default).PrivacyVersion,
        acceptedTermsLocale = termsLocale,
        acceptedPrivacyLocale = privacyLocale,
        acceptedTermsSha256 = SyntheticLegalPublication.Hash(publication ?? SyntheticLegalPublication.Default, "terms", termsLocale),
        acceptedPrivacySha256 = SyntheticLegalPublication.Hash(publication ?? SyntheticLegalPublication.Default, "privacy", privacyLocale),
    };

    public static async Task<HttpResponseMessage> RegisterAsync(Browser browser, string email, string password = ValidPassword)
    {
        await browser.FetchCsrfTokenAsync();
        return await browser.PostJsonAsync("/api/v1/auth/register", RegistrationBody(email, password));
    }

    public async Task<TestAccount> CreateConfirmedAccountAsync(string password = ValidPassword)
    {
        using var browser = new Browser(factory);
        var email = NewEmail();
        using var registered = await RegisterAsync(browser, email, password);
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var link = await ReadLatestLinkAsync(email, "EmailConfirmation");
        using var confirmed = await browser.PostJsonAsync(
            "/api/v1/auth/confirm-email",
            new { userId = link.UserId, token = link.Token, password });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        return new TestAccount(link.UserId, email, password);
    }

    /// <summary>
    /// Conta confirmada com um papel privilegiado e senha definitiva, por fixture em SQL, para os testes que partem
    /// de uma conta privilegiada já ativa. O caminho de produção é o bootstrap (<c>admin grant</c>), coberto por
    /// <see cref="AdminBootstrapTests"/>; não existe rota pública que conceda papel.
    /// </summary>
    public async Task<TestAccount> CreatePrivilegedAccountAsync(
        string role = Duora.Core.Identity.AccountRoles.Moderator, string password = ValidPassword)
    {
        var account = await CreateConfirmedAccountAsync(password);
        // O cadastro grava o papel de participante (D13); a conta privilegiada de teste troca de papel, o que o
        // bootstrap nunca faz (ele só cria conta nova, já privilegiada).
        await ScalarAsync(
            "with removed as (delete from identity.user_roles where user_id = $1 returning 1) select count(*) from removed",
            account.UserId);
        await GrantRoleAsync(account.UserId, role);
        return account;
    }

    public async Task GrantRoleAsync(Guid userId, string role)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await RoleSeparationTests.GrantAsync(connection, userId, role);
    }

    /// <summary>Sessões da conta como estão no banco: política, prova de MFA e reautenticação.</summary>
    public async Task<IReadOnlyList<(string Policy, DateTimeOffset? MfaVerifiedAt, DateTimeOffset? ReauthenticatedAt, bool Revoked)>> SessionRowsAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select policy, mfa_verified_at, reauthenticated_at, revoked_at is not null
            from identity.login_sessions where user_id = $1 order by created_at, id
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        var rows = new List<(string, DateTimeOffset?, DateTimeOffset?, bool)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetBoolean(3)));
        }

        return rows;
    }

    public static async Task<HttpResponseMessage> LoginAsync(Browser browser, string email, string password)
    {
        if (browser.Cookie("XSRF-TOKEN") is null)
        {
            await browser.FetchCsrfTokenAsync();
        }

        return await browser.PostJsonAsync("/api/v1/auth/login", new { email, password });
    }

    public async Task<Browser> SignedInBrowserAsync(TestAccount account)
    {
        var browser = new Browser(factory);
        using var response = await LoginAsync(browser, account.Email, account.Password);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await browser.FetchPrivateContextAsync();
        return browser;
    }

    public async Task<AccountLink> ReadLatestLinkAsync(string email, string kind)
    {
        return await TryReadLatestLinkAsync(email, kind)
            ?? throw new InvalidOperationException($"Nenhum e-mail {kind} na outbox para {email}.");
    }

    /// <summary>Todos os e-mails do tipo pedido para o endereço, do mais antigo ao mais novo.</summary>
    public async Task<IReadOnlyList<AccountLink>> ReadLinksAsync(string email, string kind)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select payload::text from ops.outbox_events
            where payload->>'to' = $1 and payload->>'kind' = $2
            order by occurred_at, id
            """,
            connection);
        command.Parameters.AddWithValue(email);
        command.Parameters.AddWithValue(kind);
        var links = new List<AccountLink>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            using var json = JsonDocument.Parse(reader.GetString(0));
            var root = json.RootElement;
            links.Add(new AccountLink(
                root.GetProperty("userId").GetGuid(),
                root.TryGetProperty("token", out var token) ? token.GetString() ?? "" : ""));
        }

        return links;
    }

    public async Task<AccountLink?> TryReadLatestLinkAsync(string email, string kind)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select payload::text from ops.outbox_events
            where payload->>'to' = $1 and payload->>'kind' = $2
            order by occurred_at desc, id desc
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue(email);
        command.Parameters.AddWithValue(kind);
        if (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) is not string payload)
        {
            return null;
        }

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        return new AccountLink(root.GetProperty("userId").GetGuid(), root.GetProperty("token").GetString() ?? "");
    }

    /// <summary>Idioma gravado no pedido de e-mail mais recente do tipo, ou <c>null</c> se o campo faltar.</summary>
    public async Task<string?> ReadLatestLanguageAsync(string email, string kind)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select payload->>'language' from ops.outbox_events
            where payload->>'to' = $1 and payload->>'kind' = $2
            order by occurred_at desc, id desc
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue(email);
        command.Parameters.AddWithValue(kind);
        var language = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return language as string;
    }

    public async Task<long> ScalarAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<string> ScalarTextAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Consulta sem resultado.");
    }

    public Task<long> CountUsersAsync(string email) =>
        ScalarAsync("select count(*) from identity.users where normalized_email = upper($1)", email);

    public Task<long> CountOutboxEventsAsync(string email) =>
        ScalarAsync("select count(*) from ops.outbox_events where payload->>'to' = $1", email);
}
