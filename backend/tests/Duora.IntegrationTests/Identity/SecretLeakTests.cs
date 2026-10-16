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
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = TestClock.StartingNow();
    private readonly DuoraWebFactory _factory;
    private readonly List<string> _responses = [];

    public SecretLeakTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs) { Time = _time };
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

    [Fact]
    public async Task MfaSecretsAppearOnlyInTheResponsesThatProvisionThem()
    {
        var canary = $"CANARY-{Guid.NewGuid():N}";
        var scenario = new IdentityScenario(_postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);

        var provisioning = new List<string>();
        using (var enrollment = await browser.PostJsonAsync(MfaScenario.Enrollments, body: null))
        {
            provisioning.Add(await enrollment.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var sharedKey = (string)System.Text.Json.Nodes.JsonNode.Parse(provisioning[0])!["sharedKey"]!;
        using (var confirmed = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code = Totp.Next(_time, sharedKey) }))
        {
            provisioning.Add(await confirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        await browser.FetchCsrfTokenAsync();
        using (var regenerated = await browser.PostJsonAsync(MfaScenario.RecoveryCodes, body: null))
        {
            provisioning.Add(await regenerated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var codes = System.Text.Json.Nodes.JsonNode.Parse(provisioning[2])!["recoveryCodes"]!.AsArray()
            .Select(code => (string)code!)
            .ToList();
        await RecordAsync(await browser.GetAsync("/api/v1/me"));
        await RecordAsync(await browser.GetAsync("/api/v1/me/sessions"));
        await RecordAsync(await browser.PostJsonAsync(
            MfaScenario.Reauthenticate,
            new { password = canary, twoFactorCode = Totp.Next(_time, sharedKey) }));
        await RecordAsync(await browser.PostJsonAsync(
            "/api/v1/me/password-changes",
            new { currentPassword = canary, newPassword = canary + canary }));
        using var other = new Browser(_factory);
        await RecordAsync(await MfaScenario.LoginAsync(other, account, recoveryCode: canary));
        await RecordAsync(await MfaScenario.LoginAsync(other, account, recoveryCode: codes[0]));
        await RecordAsync(await MfaScenario.LoginAsync(new Browser(_factory), account, twoFactorCode: Totp.Next(_time, sharedKey)));

        string[] secrets = [sharedKey, canary, .. codes];
        Assert.NotEmpty(_logs.Entries);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(_responses, response => response.Contains(secret, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(provisioning, response => response.Contains(canary, StringComparison.Ordinal));
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
