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
internal sealed class IdentityScenario(PostgresFixture postgres, DuoraWebFactory factory)
{
    public const string ValidPassword = "frase longa o bastante";

    public const string TermsVersion = "2026-10-01";

    public static string NewEmail() => $"pessoa-{Guid.NewGuid():N}@example.com";

    public static object RegistrationBody(string email, string password = ValidPassword) => new
    {
        email,
        password,
        acceptedTermsVersion = TermsVersion,
        acceptedPrivacyVersion = TermsVersion,
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
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
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
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
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

    public async Task<long> ScalarAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task<long> CountUsersAsync(string email) =>
        ScalarAsync("select count(*) from identity.users where normalized_email = upper($1)", email);

    public Task<long> CountOutboxEventsAsync(string email) =>
        ScalarAsync("select count(*) from ops.outbox_events where payload->>'to' = $1", email);
}
