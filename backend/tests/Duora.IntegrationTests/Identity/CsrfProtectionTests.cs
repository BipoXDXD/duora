using System.Net;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// T-13: login, logout e escrita sem token antiforgery ou com origem indevida são recusados com 403
/// <c>CSRF_VALIDATION_FAILED</c>, e nada muda no servidor.
/// </summary>
[Trait("Acceptance", "T-13")]
public sealed class CsrfProtectionTests(PostgresFixture postgres) : IAsyncDisposable
{
    private const string Forbidden = "CSRF_VALIDATION_FAILED";

    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    private IdentityScenario Scenario => new(postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task LoginWithoutTokenIsRejectedAndOpensNoSession()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = account.Email, password = account.Password },
            new MutationHeaders(SendCsrfToken: false));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, Forbidden);
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    [Fact]
    public async Task LoginWithForgedTokenIsRejected()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = account.Email, password = account.Password },
            new MutationHeaders(CsrfTokenOverride: "CfDJ8-forjado"));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, Forbidden);
    }

    [Fact]
    public async Task TokenWithoutItsCookieIsRejected()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var victim = new Browser(_factory);
        await victim.FetchCsrfTokenAsync();
        using var attacker = new Browser(_factory);

        using var response = await attacker.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = account.Email, password = account.Password },
            new MutationHeaders(CsrfTokenOverride: victim.Cookie("XSRF-TOKEN")));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, Forbidden);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://localhost")]
    [InlineData("https://localhost:5173")]
    public async Task LoginFromAnotherOriginIsRejectedEvenWithValidToken(string origin)
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = account.Email, password = account.Password },
            new MutationHeaders(Origin: origin));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, Forbidden);
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData("none")]
    public async Task LoginWithForeignFetchMetadataIsRejected(string fetchSite)
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = account.Email, password = account.Password },
            new MutationHeaders(FetchSite: fetchSite));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, Forbidden);
    }

    [Fact]
    public async Task ViteDevServerOriginIsAcceptedInDevelopment()
    {
        await using var development = new DuoraWebFactory(postgres.ConnectionString) { EnvironmentName = "Development" };
        var account = await new IdentityScenario(postgres, development).CreateConfirmedAccountAsync();
        using var browser = new Browser(development);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = account.Email, password = account.Password },
            new MutationHeaders(Origin: "https://localhost:5173"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task LogoutWithoutTokenKeepsTheSession()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var logout = await browser.PostJsonAsync(
            "/api/v1/auth/logout",
            body: null,
            new MutationHeaders(SendCsrfToken: false));
        using var me = await browser.GetAsync("/api/v1/me");

        await ProblemAssert.IsProblemAsync(logout, HttpStatusCode.Forbidden, Forbidden);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task LogoutFromAnotherOriginKeepsTheSession()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var logout = await browser.PostJsonAsync(
            "/api/v1/auth/logout",
            body: null,
            new MutationHeaders(Origin: "https://evil.example", FetchSite: "cross-site"));
        using var me = await browser.GetAsync("/api/v1/me");

        await ProblemAssert.IsProblemAsync(logout, HttpStatusCode.Forbidden, Forbidden);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task RegisterWithoutTokenCreatesNothing()
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        var email = IdentityScenario.NewEmail();

        using var response = await browser.PostJsonAsync(
            "/api/v1/auth/register",
            IdentityScenario.RegistrationBody(email),
            new MutationHeaders(SendCsrfToken: false));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, Forbidden);
        Assert.Equal(0, await Scenario.CountUsersAsync(email));
        Assert.Equal(0, await Scenario.CountOutboxEventsAsync(email));
    }

    [Fact]
    public async Task SessionRevocationWithoutTokenKeepsTheSession()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var sessionId = (await SessionTests.ListSessionsAsync(browser)).Single().Id;

        using var response = await browser.DeleteAsync(
            $"/api/v1/me/sessions/{sessionId}",
            new MutationHeaders(SendCsrfToken: false));
        using var me = await browser.GetAsync("/api/v1/me");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, Forbidden);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task TokenIssuedBeforeLoginIsReplacedByOneBoundToTheNewIdentity()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        var anonymousToken = browser.Cookie("XSRF-TOKEN");
        using var login = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        using var withOldToken = await browser.PostJsonAsync(
            "/api/v1/auth/logout",
            body: null,
            new MutationHeaders(CsrfTokenOverride: anonymousToken));
        using var withNewToken = await browser.PostJsonAsync("/api/v1/auth/logout", body: null);

        Assert.NotEqual(anonymousToken, browser.Cookie("XSRF-TOKEN"));
        await ProblemAssert.IsProblemAsync(withOldToken, HttpStatusCode.Forbidden, Forbidden);
        Assert.Equal(HttpStatusCode.NoContent, withNewToken.StatusCode);
    }
}
