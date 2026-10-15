using System.Net;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Canário <c>CANARY-&lt;uuid&gt;</c> semeado em senha e token nunca aparece em resposta (corpo ou header) nem em
/// log capturado (spec 19.2). O token real do e-mail também não vai para o log.
/// </summary>
public sealed class SecretLeakTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DuoraWebFactory _factory;
    private readonly List<string> _responses = [];

    public SecretLeakTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task CanaryPasswordsAndTokensNeverLeak()
    {
        var canary = $"CANARY-{Guid.NewGuid():N}";
        var scenario = new IdentityScenario(_postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        var email = IdentityScenario.NewEmail();

        await RecordAsync(await IdentityScenario.RegisterAsync(browser, email, canary));
        await RecordAsync(await IdentityScenario.LoginAsync(browser, account.Email, canary));
        await RecordAsync(await IdentityScenario.LoginAsync(browser, email, canary));
        await RecordAsync(await browser.PostJsonAsync(
            "/api/v1/auth/confirm-email",
            new { userId = account.UserId, token = canary, password = canary }));
        await RecordAsync(await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = account.UserId, token = canary, newPassword = canary }));
        await RecordAsync(await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = account.UserId, token = canary + "\u0000", newPassword = canary }));
        var realToken = (await scenario.ReadLatestLinkAsync(email, "EmailConfirmation")).Token;

        Assert.NotEmpty(_logs.Entries);
        Assert.DoesNotContain(_responses, response => response.Contains(canary, StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(canary, StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(realToken, StringComparison.Ordinal));
    }

    private async Task RecordAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            _responses.Add(response.Headers.ToString());
            _responses.Add(response.Content.Headers.ToString());
            _responses.Add(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }
}
