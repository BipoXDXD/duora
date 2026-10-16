using System.Net;
using Duora.Core.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Achados da revisão do #38: bloqueio que não perde contagem sob concorrência (B1), TOTP de uso único (B2), papel
/// de participante gravado no cadastro (I1), conta privilegiada fora das rotas de participante (I2), código de
/// recuperação que não vale como TOTP recente (I3) e reset que não destrava conta com MFA (I4).
/// </summary>
public sealed class SecondFactorHardeningTests : IAsyncDisposable
{
    private const int ParallelAttempts = 12;

    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time;
    private readonly DuoraWebFactory _factory;

    public SecondFactorHardeningTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _time = TestClock.StartingNow();
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = _time,
            // Os testes de concorrência mandam mais tentativas do que os limites por conta e por IP deixariam.
            Settings = new Dictionary<string, string?>
            {
                ["RateLimiting:LoginPerAccount:PermitLimit"] = "1000",
                ["RateLimiting:LoginPerAccount:Window"] = "00:15:00",
                ["RateLimiting:LoginPerIp:PermitLimit"] = "1000",
                ["RateLimiting:LoginPerIp:Window"] = "00:01:00",
                ["RateLimiting:AccountSecurityPerAccount:PermitLimit"] = "1000",
                ["RateLimiting:AccountSecurityPerAccount:Window"] = "01:00:00",
            },
        };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ParallelWrongReauthenticationsAreAllCounted()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        var responses = await Task.WhenAll(Enumerable.Range(0, ParallelAttempts).Select(_ =>
            browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = "não é a senha certa" })));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(ParallelAttempts, await AccessFailedCountAsync(account));
        Dispose(responses);
    }

    [Fact]
    public async Task ParallelWrongSecondFactorsAreAllCounted()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        var browsers = await BrowsersAsync(ParallelAttempts);

        var responses = await Task.WhenAll(browsers.Select(browser =>
            MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Wrong(_time, mfa.SharedKey))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(ParallelAttempts, await AccessFailedCountAsync(account));
        Dispose(responses);
        browsers.ForEach(browser => browser.Dispose());
    }

    [Fact]
    public async Task EnoughParallelFailuresLockTheAccount()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        // 20 falhas seguidas bloqueiam (Identity:Lockout); uma a mais confere que nenhuma se perdeu no caminho.
        var responses = await Task.WhenAll(Enumerable.Range(0, 21).Select(_ =>
            browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = "não é a senha certa" })));
        using var right = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, right.StatusCode);
        Assert.Equal(1, await Scenario.ScalarAsync(
            "select count(*) from identity.users where id = $1 and lockout_end is not null",
            account.UserId));
        Dispose(responses);
    }

    [Fact]
    public async Task AnAuthenticatorCodeWorksOnlyOnce()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        var code = Totp.Next(_time, mfa.SharedKey);

        using var first = await MfaScenario.LoginAsync(new Browser(_factory), account, twoFactorCode: code);
        using var again = await MfaScenario.LoginAsync(new Browser(_factory), account, twoFactorCode: code);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        await ProblemAssert.IsProblemAsync(again, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task TwoParallelLoginsWithTheSameCodeOpenExactlyOneSession()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        var code = Totp.Next(_time, mfa.SharedKey);
        var browsers = await BrowsersAsync(2);

        var responses = await Task.WhenAll(browsers.Select(browser =>
            MfaScenario.LoginAsync(browser, account, twoFactorCode: code)));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.NoContent));
        Dispose(responses);
        browsers.ForEach(browser => browser.Dispose());
    }

    [Fact]
    public async Task AStepBeforeOneAlreadyAcceptedIsRejected()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        var current = Totp.Next(_time, mfa.SharedKey);
        var previous = Totp.Code(mfa.SharedKey, _time.GetUtcNow().AddSeconds(-30));

        using var accepted = await MfaScenario.LoginAsync(new Browser(_factory), account, twoFactorCode: current);
        using var older = await MfaScenario.LoginAsync(new Browser(_factory), account, twoFactorCode: previous);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        await ProblemAssert.IsProblemAsync(older, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task TheCodeThatActivatedMfaCannotBeUsedToLogIn()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var sharedKey = await MfaScenario.StartEnrollmentAsync(browser);
        var code = Totp.Next(_time, sharedKey);
        using (var confirmed = await browser.PostJsonAsync(MfaScenario.Confirmations, new { code }))
        {
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        }

        using var login = await MfaScenario.LoginAsync(new Browser(_factory), account, twoFactorCode: code);

        await ProblemAssert.IsProblemAsync(login, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task AReauthenticationCodeCannotBeReplayed()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);
        using (await MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Next(_time, mfa.SharedKey)))
        {
        }

        var code = Totp.Next(_time, mfa.SharedKey);
        using var first = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password, twoFactorCode = code });
        using var again = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password, twoFactorCode = code });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        await ProblemAssert.IsProblemAsync(again, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task CodesTypedWithSpacesOrWithoutTheHyphenAreAccepted()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        var code = Totp.Next(_time, mfa.SharedKey);

        using var spaced = await MfaScenario.LoginAsync(new Browser(_factory), account, twoFactorCode: $"{code[..3]} {code[3..]}");
        using var withoutHyphen = await MfaScenario.LoginAsync(
            new Browser(_factory),
            account,
            recoveryCode: mfa.RecoveryCodes[0].Replace("-", "", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.NoContent, spaced.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, withoutHyphen.StatusCode);
    }

    [Fact]
    public async Task ARightPasswordWithAWrongSecondFactorIsAudited()
    {
        var (account, mfa) = await AccountWithMfaAsync();

        using var response = await MfaScenario.LoginAsync(
            new Browser(_factory),
            account,
            twoFactorCode: Totp.Wrong(_time, mfa.SharedKey));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            "identity.second_factor_failed",
            await MfaEnrollmentTests.AuditActionsAsync(_postgres, account.UserId));
    }

    [Fact]
    public async Task APasswordResetDoesNotUnlockAnAccountWithMfa()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            var browser = new Browser(_factory);
            return await MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Wrong(_time, mfa.SharedKey));
        }));
        Dispose(responses);
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        using (await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email = account.Email }))
        {
        }

        var link = await Scenario.ReadLatestLinkAsync(account.Email, "PasswordReset");
        using var reset = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = link.UserId, token = link.Token, newPassword = "uma frase nova e longa" });
        using var login = await MfaScenario.LoginAsync(
            browser,
            account with { Password = "uma frase nova e longa" },
            twoFactorCode: Totp.Next(_time, mfa.SharedKey));

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        await ProblemAssert.IsProblemAsync(login, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task RegistrationGrantsTheParticipantRoleSoNoPrivilegedRoleCanBeAdded()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();

        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => Scenario.GrantRoleAsync(account.UserId, AccountRoles.Moderator));

        Assert.Equal("ck_user_roles_privileged_is_not_participant", error.ConstraintName);
        using var browser = await Scenario.SignedInBrowserAsync(account);
        using var me = await browser.GetAsync("/api/v1/me");
        Assert.Equal(
            [AccountRoles.Participant],
            (await MfaScenario.ReadObjectAsync(me))["roles"]!.AsArray().Select(role => (string)role!));
    }

    [Fact]
    public async Task APrivilegedAccountIsForbiddenOnParticipantRoutes()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, _time);

        using var profile = await browser.PatchRawJsonAsync("/api/v1/me/profile", """{"displayName":"Moderação"}""", "\"1\"");
        using var block = await browser.PutAsync($"/api/v1/me/blocks/{Guid.NewGuid()}");
        using var sessions = await browser.GetAsync("/api/v1/me/sessions");

        await ProblemAssert.IsProblemAsync(profile, HttpStatusCode.Forbidden, "FORBIDDEN");
        await ProblemAssert.IsProblemAsync(block, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
    }

    [Fact]
    public async Task ALoginWithARecoveryCodeIsNotARecentAuthenticatorProof()
    {
        var (account, mfa) = await AccountWithMfaAsync();
        using var browser = new Browser(_factory);
        using (var login = await MfaScenario.LoginAsync(browser, account, recoveryCode: mfa.RecoveryCodes[2]))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        using var regenerate = await browser.PostJsonAsync(MfaScenario.RecoveryCodes, body: null);

        await ProblemAssert.IsProblemAsync(regenerate, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
    }

    [Fact]
    public async Task ConcurrentActivationsAndRegenerationsNeverFailWithAServerError()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var sharedKey = await MfaScenario.StartEnrollmentAsync(browser);
        var code = Totp.Next(_time, sharedKey);

        var activations = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            browser.PostJsonAsync(MfaScenario.Confirmations, new { code })));

        Assert.Equal(1, activations.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.All(
            activations.Where(response => response.StatusCode != HttpStatusCode.OK),
            response => Assert.True(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized, $"{(int)response.StatusCode}"));
        Dispose(activations);
    }

    [Fact]
    public async Task ParallelRecoveryCodeRegenerationsLeaveOneValidSet()
    {
        var (account, mfa, browser) = await SignedInAccountWithMfaAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            browser.PostJsonAsync(MfaScenario.RecoveryCodes, body: null)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(AccountSecurityRecoveryCodeCount, await Scenario.ScalarAsync(
            """
            select coalesce(array_length(string_to_array(value, ';'), 1), 0) from identity.user_tokens
            where user_id = $1 and name = 'RecoveryCodeHashes'
            """,
            account.UserId));
        Dispose(responses);
        browser.Dispose();
        Assert.NotEmpty(mfa.RecoveryCodes);
    }

    [Fact]
    public async Task ARenewedSessionKeepsTheAbsoluteLimitOfTheOriginalLogin()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(11); elapsed += TimeSpan.FromMinutes(20))
        {
            _time.Advance(TimeSpan.FromMinutes(20));
            using var active = await browser.GetAsync("/api/v1/me");
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        }

        using (var reauthenticated = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password }))
        {
            Assert.Equal(HttpStatusCode.NoContent, reauthenticated.StatusCode);
        }

        using (var changed = await browser.PostJsonAsync(
            "/api/v1/me/password-changes",
            new { currentPassword = account.Password, newPassword = "uma frase nova e longa" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        }

        // Renovada às 11 h; uma sessão nova iria até as 23 h, mas a renovada termina nas 12 h do login original.
        _time.Advance(TimeSpan.FromMinutes(25));
        using var at1125 = await browser.GetAsync("/api/v1/me");
        _time.Advance(TimeSpan.FromMinutes(25));
        using var at1150 = await browser.GetAsync("/api/v1/me");
        _time.Advance(TimeSpan.FromMinutes(25));
        using var pastTheOriginalLimit = await browser.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, at1125.StatusCode);
        Assert.Equal(HttpStatusCode.OK, at1150.StatusCode);
        await ProblemAssert.IsProblemAsync(pastTheOriginalLimit, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    private const long AccountSecurityRecoveryCodeCount = 10;

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
        var mfa = await MfaScenario.EnableAsync(browser, _time);
        return (account, mfa, browser);
    }

    private async Task<List<Browser>> BrowsersAsync(int count)
    {
        var browsers = Enumerable.Range(0, count).Select(_ => new Browser(_factory)).ToList();
        foreach (var browser in browsers)
        {
            await browser.FetchCsrfTokenAsync();
        }

        return browsers;
    }

    private Task<long> AccessFailedCountAsync(TestAccount account) =>
        Scenario.ScalarAsync("select access_failed_count from identity.users where id = $1", account.UserId);

    private static void Dispose(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
