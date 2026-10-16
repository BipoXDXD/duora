using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Login com segundo fator (API-A04), códigos de recuperação (A14) e desativação (A15). A senha certa sem segundo
/// fator responde <c>MFA_REQUIRED</c> sem criar sessão; código errado conta no mesmo bloqueio da senha (TM-01).
/// </summary>
public sealed class MfaLoginTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time;
    private readonly DuoraWebFactory _factory;

    public MfaLoginTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = _time,
            // O teste do bloqueio faz mais tentativas do que o rate limit por conta deixaria.
            Settings = new Dictionary<string, string?>
            {
                ["RateLimiting:LoginPerAccount:PermitLimit"] = "100",
                ["RateLimiting:LoginPerAccount:Window"] = "00:15:00",
                ["RateLimiting:LoginPerIp:PermitLimit"] = "1000",
                ["RateLimiting:LoginPerIp:Window"] = "00:01:00",
            },
        };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task TheRightPasswordWithoutASecondFactorIsMfaRequiredAndOpensNoSession()
    {
        var (account, _) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);
        var sessionsBefore = (await Scenario.SessionRowsAsync(account.UserId)).Count;

        using var response = await MfaScenario.LoginAsync(browser, account);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "MFA_REQUIRED");
        Assert.Null(browser.Cookie("__Host-duora"));
        Assert.Equal(sessionsBefore, (await Scenario.SessionRowsAsync(account.UserId)).Count);
    }

    [Fact]
    public async Task TheAuthenticatorCodeOpensASessionWithTheMfaProof()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);
        _time.Advance(TimeSpan.FromSeconds(1));

        using var response = await MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Now(mfa.SharedKey));
        using var me = await browser.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var session = (await Scenario.SessionRowsAsync(account.UserId))[^1];
        Assert.Equal("Normal", session.Policy);
        Assert.Equal(_time.GetUtcNow(), session.MfaVerifiedAt);
    }

    [Fact]
    public async Task AWrongCodeIsAnInvalidCredentialAndCountsTowardTheLockout()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);

        using var response = await MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Wrong(mfa.SharedKey));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Null(browser.Cookie("__Host-duora"));
        Assert.Equal(1, await AccessFailedCountAsync(account));
    }

    [Fact]
    public async Task AValidCodeWithTheWrongPasswordIsAnInvalidCredential()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);

        using var response = await MfaScenario.LoginAsync(
            browser,
            account with { Password = "outra frase longa qualquer" },
            twoFactorCode: Totp.Now(mfa.SharedKey));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task TwentyWrongCodesLockTheAccountEvenForTheRightCode()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var wrong = await MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Wrong(mfa.SharedKey));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var right = await MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Now(mfa.SharedKey));

        await ProblemAssert.IsProblemAsync(right, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task ASuccessfulSecondFactorClearsPreviousFailures()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);
        using (await MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Wrong(mfa.SharedKey)))
        {
        }

        using var right = await MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Now(mfa.SharedKey));

        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.Equal(0, await AccessFailedCountAsync(account));
    }

    [Fact]
    public async Task ARecoveryCodeWorksOnceAndItsUseIsAudited()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);
        var code = mfa.RecoveryCodes[3];

        using var first = await MfaScenario.LoginAsync(browser, account, recoveryCode: code);
        using var again = await MfaScenario.LoginAsync(new Browser(_factory), account, recoveryCode: code);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        await ProblemAssert.IsProblemAsync(again, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Contains("identity.mfa_recovery_code_used", await MfaEnrollmentTests.AuditActionsAsync(_postgres, account.UserId));
    }

    [Fact]
    public async Task ARecoveryCodeIsAcceptedInLowerCaseAndWithSurroundingSpaces()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);

        using var response = await MfaScenario.LoginAsync(
            browser,
            account,
            recoveryCode: $"  {mfa.RecoveryCodes[0].ToLowerInvariant()} ");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task TheSameRecoveryCodeUsedConcurrentlyOpensOnlyOneSession()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        var browsers = Enumerable.Range(0, 4).Select(_ => new Browser(_factory)).ToList();
        foreach (var browser in browsers)
        {
            await browser.FetchCsrfTokenAsync();
        }

        var responses = await Task.WhenAll(
            browsers.Select(browser => MfaScenario.LoginAsync(browser, account, recoveryCode: mfa.RecoveryCodes[1])));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.NoContent));
        Assert.All(
            responses.Where(response => response.StatusCode != HttpStatusCode.NoContent),
            response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        browsers.ForEach(browser => browser.Dispose());
    }

    [Theory]
    [InlineData("12345", null)]
    [InlineData("abcdef", null)]
    [InlineData("123456", "ABCDE-FGHIJ")]
    [InlineData(null, "\u0000")]
    [InlineData(null, "123456789012345678901234567890123")]
    public async Task MalformedSecondFactorsAreValidationErrors(string? twoFactorCode, string? recoveryCode)
    {
        var (account, _) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);

        using var response = await MfaScenario.LoginAsync(browser, account, twoFactorCode, recoveryCode);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task NewRecoveryCodesReplaceTheWholePreviousSet()
    {
        var (account, mfa, browser) = await SignedInAccountWithMfaAsync();

        using var regenerated = await browser.PostJsonAsync(MfaScenario.RecoveryCodes, body: null);

        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
        Assert.Equal("no-store", regenerated.Headers.CacheControl?.ToString());
        var codes = (await MfaScenario.ReadObjectAsync(regenerated))["recoveryCodes"]!.AsArray()
            .Select(code => (string)code!)
            .ToList();
        Assert.Equal(10, codes.Distinct(StringComparer.Ordinal).Count());
        using var oldCode = await MfaScenario.LoginAsync(new Browser(_factory), account, recoveryCode: mfa.RecoveryCodes[0]);
        using var newCode = await MfaScenario.LoginAsync(new Browser(_factory), account, recoveryCode: codes[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, oldCode.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, newCode.StatusCode);
        Assert.Contains(
            "identity.mfa_recovery_codes_regenerated",
            await MfaEnrollmentTests.AuditActionsAsync(_postgres, account.UserId));
        browser.Dispose();
    }

    [Fact]
    public async Task NewRecoveryCodesNeedActiveMfa()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await browser.PostJsonAsync(MfaScenario.RecoveryCodes, body: null);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "MFA_NOT_ENABLED");
    }

    [Fact]
    public async Task NewRecoveryCodesNeedARecentAuthentication()
    {
        var (_, _, browser) = await SignedInAccountWithMfaAsync();
        _time.Advance(TimeSpan.FromMinutes(6));

        using var response = await browser.PostJsonAsync(MfaScenario.RecoveryCodes, body: null);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        browser.Dispose();
    }

    [Fact]
    public async Task AParticipantCanDisableMfaAndLogInWithThePasswordAgain()
    {
        var (account, mfa, browser) = await SignedInAccountWithMfaAsync();
        using var other = new Browser(_factory);
        using (await MfaScenario.LoginAsync(other, account, twoFactorCode: Totp.Now(mfa.SharedKey)))
        {
        }

        using var disabled = await browser.DeleteAsync(MfaScenario.Mfa);
        using var current = await browser.GetAsync("/api/v1/me");
        using var otherMe = await other.GetAsync("/api/v1/me");
        using var passwordOnly = await MfaScenario.LoginAsync(new Browser(_factory), account);
        using var oldCode = await MfaScenario.LoginAsync(new Browser(_factory), account, recoveryCode: mfa.RecoveryCodes[0]);

        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.False((bool)(await MfaScenario.ReadObjectAsync(current))["mfaEnabled"]!);
        Assert.Equal(HttpStatusCode.Unauthorized, otherMe.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, passwordOnly.StatusCode);
        Assert.Contains("identity.mfa_disabled", await MfaEnrollmentTests.AuditActionsAsync(_postgres, account.UserId));
        browser.Dispose();
    }

    [Fact]
    public async Task DisablingMfaThatIsNotActiveIsANoOp()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await browser.DeleteAsync(MfaScenario.Mfa);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain("identity.mfa_disabled", await MfaEnrollmentTests.AuditActionsAsync(_postgres, account.UserId));
    }

    private async Task<(TestAccount Account, EnabledMfa Mfa)> AccountWithMfaAsync()
    {
        var (account, mfa, browser) = await SignedInAccountWithMfaAsync();
        browser.Dispose();
        return (account, mfa);
    }

    private async Task<(TestAccount Account, EnabledMfa Mfa, Browser Browser)> SignedInAccountWithMfaAsync()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        var browser = await Scenario.SignedInBrowserAsync(account);
        var mfa = await MfaScenario.EnableAsync(browser);
        return (account, mfa, browser);
    }

    private Task<long> AccessFailedCountAsync(TestAccount account) =>
        Scenario.ScalarAsync("select access_failed_count from identity.users where id = $1", account.UserId);
}
