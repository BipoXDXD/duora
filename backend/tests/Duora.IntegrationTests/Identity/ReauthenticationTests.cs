using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Reautenticação (API-A13) e troca de senha (API-A16). A prova fica na sessão, no servidor; a janela de 5 min é
/// conferida a cada requisição. Conta com MFA reautentica com senha e TOTP.
/// </summary>
public sealed class ReauthenticationTests : IAsyncDisposable
{
    private const string PasswordChanges = "/api/v1/me/password-changes";
    private const string NewPassword = "uma frase nova e bem longa";

    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time;
    private readonly DuoraWebFactory _factory;

    public ReauthenticationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ThePasswordRenewsTheRecentAuthenticationOfTheCurrentSession()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        _time.Advance(TimeSpan.FromMinutes(10));
        using var stale = await browser.PostJsonAsync(MfaScenario.Enrollments, body: null);

        using var reauthenticated = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password });
        using var fresh = await browser.PostJsonAsync(MfaScenario.Enrollments, body: null);

        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        Assert.Equal(HttpStatusCode.NoContent, reauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Contains("identity.reauthenticated", await MfaEnrollmentTests.AuditActionsAsync(_postgres, account.UserId));
    }

    [Fact]
    public async Task AWrongPasswordIsRejectedAndCountsTowardTheLockout()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        _time.Advance(TimeSpan.FromMinutes(10));

        using var response = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = "não é a senha certa" });
        using var still = await browser.PostJsonAsync(MfaScenario.Enrollments, body: null);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.Unauthorized, still.StatusCode);
        Assert.Equal(1, await Scenario.ScalarAsync("select access_failed_count from identity.users where id = $1", account.UserId));
    }

    [Fact]
    public async Task AnAccountWithMfaNeedsTheAuthenticatorCodeToReauthenticate()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var mfa = await MfaScenario.EnableAsync(browser);
        _time.Advance(TimeSpan.FromMinutes(10));

        using var passwordOnly = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password });
        using var wrongCode = await browser.PostJsonAsync(
            MfaScenario.Reauthenticate,
            new { password = account.Password, twoFactorCode = Totp.Wrong(mfa.SharedKey) });
        using var withCode = await browser.PostJsonAsync(
            MfaScenario.Reauthenticate,
            new { password = account.Password, twoFactorCode = Totp.Now(mfa.SharedKey) });

        await ProblemAssert.IsProblemAsync(passwordOnly, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        await ProblemAssert.IsProblemAsync(wrongCode, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, withCode.StatusCode);
        var session = Assert.Single(await Scenario.SessionRowsAsync(account.UserId), row => !row.Revoked);
        Assert.Equal(_time.GetUtcNow(), session.MfaVerifiedAt);
        Assert.Equal(_time.GetUtcNow(), session.ReauthenticatedAt);
    }

    [Fact]
    public async Task ReauthenticationNeedsASession()
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var response = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = "qualquer coisa" });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task ChangingThePasswordRenewsThisSessionAndEndsTheOthers()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var other = await Scenario.SignedInBrowserAsync(account);
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var cookieBefore = browser.Cookie("__Host-duora");

        using var changed = await browser.PostJsonAsync(
            PasswordChanges,
            new { currentPassword = account.Password, newPassword = NewPassword });
        using var current = await browser.GetAsync("/api/v1/me");
        using var otherMe = await other.GetAsync("/api/v1/me");
        using var oldPassword = await IdentityScenario.LoginAsync(new Browser(_factory), account.Email, account.Password);
        using var newPassword = await IdentityScenario.LoginAsync(new Browser(_factory), account.Email, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.NotEqual(cookieBefore, browser.Cookie("__Host-duora"));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherMe.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, newPassword.StatusCode);
        Assert.Contains("identity.password_changed", await MfaEnrollmentTests.AuditActionsAsync(_postgres, account.UserId));
    }

    [Fact]
    public async Task AWrongCurrentPasswordChangesNothing()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await browser.PostJsonAsync(
            PasswordChanges,
            new { currentPassword = "não é a senha atual", newPassword = NewPassword });
        using var oldPassword = await IdentityScenario.LoginAsync(new Browser(_factory), account.Email, account.Password);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, oldPassword.StatusCode);
    }

    [Theory]
    [InlineData("curta")]
    [InlineData("frase longa com \u0000 no meio")]
    public async Task ANewPasswordOutsideThePolicyIsAValidationError(string newPassword)
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await browser.PostJsonAsync(
            PasswordChanges,
            new { currentPassword = account.Password, newPassword });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task ChangingThePasswordNeedsARecentAuthentication()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        _time.Advance(TimeSpan.FromMinutes(6));

        using var response = await browser.PostJsonAsync(
            PasswordChanges,
            new { currentPassword = account.Password, newPassword = NewPassword });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
    }

    [Fact]
    public async Task TheAccountSecurityOperationsAreLimitedPerSession()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.StartEnrollmentAsync(browser);
        for (var attempt = 1; attempt < 10; attempt++)
        {
            using var allowed = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code = "000000" });
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var limited = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code = "000000" });

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.True(ProblemAssert.RetryAfterSeconds(limited) > 0);
    }
}
