using System.Net;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// T-15, o resto do que o key ring protege: o par antiforgery e os links de e-mail emitidos antes de perder ou
/// revogar as chaves falham de forma limpa (403 e 400), nunca 500, e a pessoa se recupera pelo caminho normal.
/// </summary>
public sealed class KeyRingLossTests : IDisposable
{
    private const string CsrfFailed = "CSRF_VALIDATION_FAILED";
    private const string InvalidToken = "INVALID_TOKEN";
    private const string LoginPath = "/api/v1/auth/login";

    private readonly PostgresFixture _postgres;
    private readonly KeyRingHosts _hosts;
    private readonly DirectoryInfo _volume = Directory.CreateTempSubdirectory("duora-keys-volume-");
    private readonly DirectoryInfo _emptyVolume = Directory.CreateTempSubdirectory("duora-keys-empty-");

    public KeyRingLossTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _hosts = new KeyRingHosts(postgres);
    }

    public void Dispose()
    {
        _volume.Delete(recursive: true);
        _emptyVolume.Delete(recursive: true);
    }

    [Fact]
    public async Task AntiforgeryTokenFromALostKeyRingIsRejectedWithCsrfFailure()
    {
        var cookies = await AntiforgeryCookiesFromAFreshHostAsync();

        await using var recreated = _hosts.HostOn(_emptyVolume);
        using var browser = BrowserWith(recreated, cookies);
        using var response = await LoginWithUnknownAccountAsync(browser);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, CsrfFailed);
    }

    [Fact]
    public async Task AntiforgeryTokenFromARevokedKeyRingIsRejectedWithCsrfFailure()
    {
        var cookies = await AntiforgeryCookiesFromAFreshHostAsync();
        await using (var compromised = _hosts.HostOn(_volume))
        {
            compromised.Services.GetRequiredService<IKeyManager>()
                .RevokeAllKeys(DateTimeOffset.UtcNow, "T-15: resposta a comprometimento do key ring");
        }

        await using var recreated = _hosts.HostOn(_volume);
        using var browser = BrowserWith(recreated, cookies);
        using var response = await LoginWithUnknownAccountAsync(browser);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, CsrfFailed);
    }

    [Fact]
    public async Task FetchingTheCsrfTokenWithTheStaleCookieIssuesAFreshWorkingPair()
    {
        var cookies = await AntiforgeryCookiesFromAFreshHostAsync();
        var staleToken = Assert.Single(cookies, cookie => cookie.Name == "XSRF-TOKEN").Value;

        await using var recreated = _hosts.HostOn(_emptyVolume);
        using var browser = BrowserWith(recreated, cookies);
        using var csrf = await browser.GetAsync("/api/v1/auth/csrf");
        using var login = await LoginWithUnknownAccountAsync(browser);

        Assert.Equal(HttpStatusCode.NoContent, csrf.StatusCode);
        Assert.NotEqual(staleToken, browser.Cookie("XSRF-TOKEN"));
        await ProblemAssert.IsProblemAsync(login, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task ConfirmationLinkFromALostKeyRingIsAnInvalidToken()
    {
        var email = IdentityScenario.NewEmail();
        AccountLink link;
        await using (var before = _hosts.HostOn(_volume))
        {
            using var registering = new Browser(before);
            using var registered = await IdentityScenario.RegisterAsync(registering, email);
            Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
            link = await new IdentityScenario(_postgres, before).CreateLegacyConfirmationProofAsync(email);
        }

        await using var recreated = _hosts.HostOn(_emptyVolume);
        using var browser = new Browser(recreated);
        await browser.FetchCsrfTokenAsync();
        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/confirm-email",
            new { userId = link.UserId, token = link.Token, password = IdentityScenario.ValidPassword });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, InvalidToken);
    }

    [Fact]
    public async Task PasswordResetLinkFromALostKeyRingIsAnInvalidToken()
    {
        AccountLink link;
        await using (var before = _hosts.HostOn(_volume))
        {
            var scenario = new IdentityScenario(_postgres, before);
            var account = await scenario.CreateConfirmedAccountAsync();
            using var requesting = new Browser(before);
            await requesting.FetchCsrfTokenAsync();
            using var requested = await requesting.PostJsonAsync(
                "/api/v1/auth/password-reset-requests",
                new { email = account.Email });
            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
            link = await scenario.ReadLatestLinkAsync(account.Email, "PasswordReset");
        }

        await using var recreated = _hosts.HostOn(_emptyVolume);
        using var browser = new Browser(recreated);
        await browser.FetchCsrfTokenAsync();
        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = link.UserId, token = link.Token, newPassword = "outra frase longa o bastante" });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, InvalidToken);
    }

    private async Task<IReadOnlyList<Cookie>> AntiforgeryCookiesFromAFreshHostAsync()
    {
        await using var host = _hosts.HostOn(_volume);
        using var browser = new Browser(host);
        await browser.FetchCsrfTokenAsync();
        return [.. browser.Cookies.GetAllCookies().Cast<Cookie>()];
    }

    private static Browser BrowserWith(DuoraWebFactory host, IReadOnlyList<Cookie> cookies)
    {
        var browser = new Browser(host);
        foreach (var cookie in cookies)
        {
            browser.Cookies.Add(cookie);
        }

        return browser;
    }

    private static Task<HttpResponseMessage> LoginWithUnknownAccountAsync(Browser browser) =>
        browser.PostJsonAsync(
            LoginPath,
            new { email = IdentityScenario.NewEmail(), password = IdentityScenario.ValidPassword });
}
