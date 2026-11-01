using System.Net;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Profiles;

public sealed class PrivilegedLocaleTests : IAsyncDisposable
{
    private const string LocalePath = "/api/v1/me/locale";
    private readonly FakeTimeProvider _time = TestClock.StartingNow();
    private readonly DuoraWebFactory _factory;
    private readonly PostgresFixture _postgres;

    public PrivilegedLocaleTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private IdentityScenario Scenario => new(_postgres, _factory);

    [Theory]
    [InlineData("operator")]
    [InlineData("moderator")]
    [InlineData("product_admin")]
    public async Task APrivilegedAccountCanPersistItsDisplayLocaleWithoutParticipantAccessOrDisablingMfa(string role)
    {
        var account = await Scenario.CreatePrivilegedAccountAsync(role);
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, _time);

        using var current = await browser.GetAsync(LocalePath);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("\"0\"", current.Headers.ETag!.ToString());

        using var saved = await SaveAsync(browser, "pt-BR", "0");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("\"1\"", saved.Headers.ETag!.ToString());
        Assert.Equal("pt-BR", (string?)(await MfaScenario.ReadObjectAsync(saved))["locale"]);

        using var me = await browser.GetAsync("/api/v1/me");
        var own = await MfaScenario.ReadObjectAsync(me);
        Assert.Equal("pt-BR", (string?)own["preferredLocale"]);
        Assert.True((bool)own["mfaEnabled"]!);
        Assert.Equal(role, (string?)Assert.Single(own["roles"]!.AsArray()));

        using var preferences = await browser.GetAsync("/api/v1/me/preferences");
        await ProblemAssert.IsProblemAsync(preferences, HttpStatusCode.Forbidden, "FORBIDDEN");
        using var disable = await browser.DeleteAsync(MfaScenario.Mfa);
        await ProblemAssert.IsProblemAsync(disable, HttpStatusCode.Forbidden, "MFA_REQUIRED_BY_ROLE");
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("moderator")]
    [InlineData("product_admin")]
    public async Task LocaleDoesNotBypassMandatoryMfaEnrollment(string role)
    {
        var account = await Scenario.CreatePrivilegedAccountAsync(role);
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var read = await browser.GetAsync(LocalePath);
        await ProblemAssert.IsProblemAsync(read, HttpStatusCode.Forbidden, "MFA_ENROLLMENT_REQUIRED");
        using var write = await SaveAsync(browser, "pt-BR", "0");
        await ProblemAssert.IsProblemAsync(write, HttpStatusCode.Forbidden, "MFA_ENROLLMENT_REQUIRED");
    }

    [Fact]
    public async Task PrivilegedLocaleStillRequiresCsrfAndTheCurrentVersion()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync("operator");
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, _time);

        using var noCsrf = await browser.PutAsync(LocalePath, """{"locale":"pt-BR"}""",
            new MutationHeaders(SendCsrfToken: false, Extra: new Dictionary<string, string> { ["If-Match"] = "\"0\"" }));
        await ProblemAssert.IsProblemAsync(noCsrf, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        using var noVersion = await browser.PutAsync(LocalePath, """{"locale":"pt-BR"}""");
        Assert.Equal((HttpStatusCode)428, noVersion.StatusCode);
        using var stale = await SaveAsync(browser, "pt-BR", "1");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        using var unchanged = await browser.GetAsync(LocalePath);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal("\"0\"", unchanged.Headers.ETag!.ToString());
        Assert.Equal("en-US", (string?)(await MfaScenario.ReadObjectAsync(unchanged))["locale"]);
    }

    private static Task<HttpResponseMessage> SaveAsync(Browser browser, string locale, string version) =>
        browser.PutAsync(LocalePath, $$"""{"locale":"{{locale}}"}""",
            new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = $"\"{version}\"" }));
}
