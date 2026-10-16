using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Duora.Contracts.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Cookie de sessão, fixation, revogação e expiração (T-14, parte HTTP: a requisição seguinte responde 401).
/// </summary>
public sealed class SessionTests : IAsyncDisposable
{
    private const string SessionCookie = "__Host-duora";

    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time;
    private readonly DuoraWebFactory _factory;

    public SessionTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        // Instante real só como ponto de partida, para as chaves de Data Protection não nascerem "no futuro"; os
        // testes dependem apenas dos avanços explícitos.
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task SessionCookieHasExactlyTheHostOnlyAttributes()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(_factory);

        using var response = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var attributes = SetCookieAttributes(response, SessionCookie);
        Assert.Equal(["httponly", "path=/", "samesite=lax", "secure"], attributes);
    }

    [Fact]
    public async Task CsrfCookieIsReadableStrictAndNotCached()
    {
        using var browser = new Browser(_factory);

        using var response = await browser.GetAsync("/api/v1/auth/csrf");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["path=/", "samesite=strict", "secure"], SetCookieAttributes(response, "XSRF-TOKEN"));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task MeWithoutSessionIsUnauthorizedProblemNotRedirect()
    {
        using var browser = new Browser(_factory);

        using var response = await browser.GetAsync("/api/v1/me");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task MeReturnsOnlyTheContractFields()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await browser.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await ReadObjectAsync(response);
        Assert.Equal(
            ["email", "emailConfirmed", "id", "mfaEnabled", "roles", "status"],
            body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.False((bool?)body["mfaEnabled"]);
        Assert.Equal(["participant"], body["roles"]!.AsArray().Select(role => (string?)role));
        Assert.Equal(account.UserId, body["id"]!.GetValue<Guid>());
        Assert.Equal(account.Email, (string?)body["email"]);
        Assert.Equal("PendingEligibility", (string?)body["status"]);
        Assert.True((bool?)body["emailConfirmed"]);
    }

    [Fact]
    public async Task SessionIdChangesOnLoginAndThePreviousCookieStopsWorking()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var firstCookie = browser.Cookie(SessionCookie);

        using var secondLogin = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);
        var secondCookie = browser.Cookie(SessionCookie);
        using var replay = CloneWithSessionCookie(firstCookie);
        using var replayed = await replay.GetAsync("/api/v1/me");
        using var current = await browser.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.NoContent, secondLogin.StatusCode);
        Assert.NotNull(secondCookie);
        Assert.NotEqual(firstCookie, secondCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task LoginAsAnotherAccountReplacesTheSessionOfTheFirst()
    {
        var first = await Scenario.CreateConfirmedAccountAsync();
        var second = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(first);
        var firstCookie = browser.Cookie(SessionCookie);

        using var login = await IdentityScenario.LoginAsync(browser, second.Email, second.Password);
        using var replay = CloneWithSessionCookie(firstCookie);
        using var replayed = await replay.GetAsync("/api/v1/me");
        var me = await ReadObjectAsync(await browser.GetAsync("/api/v1/me"));

        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal(second.UserId, me["id"]!.GetValue<Guid>());
    }

    [Fact]
    [Trait("Acceptance", "T-14")]
    public async Task LogoutRevokesTheSessionOnTheServer()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var cookie = browser.Cookie(SessionCookie);

        using var logout = await browser.PostJsonAsync("/api/v1/auth/logout", body: null);
        using var replay = CloneWithSessionCookie(cookie);
        using var replayed = await replay.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        await ProblemAssert.IsProblemAsync(replayed, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    [Trait("Acceptance", "T-14")]
    public async Task SessionRevokedFromAnotherDeviceAnswers401OnTheNextRequest()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var laptop = await Scenario.SignedInBrowserAsync(account);
        using var phone = await Scenario.SignedInBrowserAsync(account);
        var laptopSession = (await ListSessionsAsync(phone)).Single(session => !session.Current);

        using var revoke = await phone.DeleteAsync($"/api/v1/me/sessions/{laptopSession.Id}");
        using var laptopMe = await laptop.GetAsync("/api/v1/me");
        using var phoneMe = await phone.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await ProblemAssert.IsProblemAsync(laptopMe, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Equal(HttpStatusCode.OK, phoneMe.StatusCode);
    }

    [Fact]
    [Trait("Acceptance", "T-14")]
    public async Task IdleSessionExpiresAfterThirtyMinutes()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        _time.Advance(TimeSpan.FromMinutes(29));
        using var stillActive = await browser.GetAsync("/api/v1/me");
        _time.Advance(TimeSpan.FromMinutes(30));
        using var expired = await browser.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
        await ProblemAssert.IsProblemAsync(expired, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    [Trait("Acceptance", "T-14")]
    public async Task ActivitySlidesTheIdleLimitButNeverPastTwelveHours()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var step = TimeSpan.FromMinutes(25);
        var elapsed = TimeSpan.Zero;

        while (elapsed + step < TimeSpan.FromHours(12))
        {
            _time.Advance(step);
            elapsed += step;
            using var active = await browser.GetAsync("/api/v1/me");
            Assert.True(active.StatusCode == HttpStatusCode.OK, $"Sessão caiu com {elapsed} de uso contínuo.");
        }

        _time.Advance(step);
        using var pastAbsolute = await browser.GetAsync("/api/v1/me");

        await ProblemAssert.IsProblemAsync(pastAbsolute, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task SessionListMarksTheCurrentSessionAndShowsOnlyContractFields()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await browser.GetAsync("/api/v1/me/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadObjectAsync(response);
        Assert.Equal(["items"], body.Select(p => p.Key));
        var item = Assert.Single(body["items"]!.AsArray())!.AsObject();
        Assert.Equal(
            ["createdAt", "current", "description", "id", "lastSeenAt"],
            item.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.True((bool?)item["current"]);
        Assert.EndsWith("Z", (string?)item["createdAt"], StringComparison.Ordinal);
        Assert.EndsWith("Z", (string?)item["lastSeenAt"], StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherAccountCannotRevokeMySessionAndGets404()
    {
        var owner = await Scenario.CreateConfirmedAccountAsync();
        var intruder = await Scenario.CreateConfirmedAccountAsync();
        using var ownerBrowser = await Scenario.SignedInBrowserAsync(owner);
        using var intruderBrowser = await Scenario.SignedInBrowserAsync(intruder);
        var ownerSession = (await ListSessionsAsync(ownerBrowser)).Single();

        using var attempt = await intruderBrowser.DeleteAsync($"/api/v1/me/sessions/{ownerSession.Id}");
        using var unknown = await intruderBrowser.DeleteAsync($"/api/v1/me/sessions/{Guid.NewGuid()}");
        using var ownerMe = await ownerBrowser.GetAsync("/api/v1/me");

        var attemptBody = await ProblemAssert.IsProblemAsync(attempt, HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.IsProblemAsync(unknown, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.DoesNotContain(owner.Email, attemptBody.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, ownerMe.StatusCode);
        Assert.Single(await ListSessionsAsync(ownerBrowser));
    }

    internal static async Task<IReadOnlyList<SessionResponse>> ListSessionsAsync(Browser browser)
    {
        using var response = await browser.GetAsync("/api/v1/me/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<SessionListResponse>(TestContext.Current.CancellationToken);
        return list?.Items ?? throw new InvalidOperationException("Lista de sessões vazia.");
    }

    private Browser CloneWithSessionCookie(string? sessionCookie)
    {
        var clone = new Browser(_factory);
        clone.Cookies.Add(Browser.BaseAddress, new Cookie(SessionCookie, sessionCookie ?? ""));
        return clone;
    }

    /// <summary>Atributos do Set-Cookie do cookie pedido, em minúsculas e ordenados, sem o par nome=valor.</summary>
    private static string[] SetCookieAttributes(HttpResponseMessage response, string name)
    {
        var header = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(name + "=", StringComparison.Ordinal) && !value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        return [.. header.Split(';').Skip(1).Select(part => part.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal)];
    }

    private static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))?.AsObject()
            ?? throw new InvalidOperationException("Corpo vazio.");
}
