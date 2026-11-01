using System.Net;
using System.Text.Json;
using Duora.Infrastructure.Outbox;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>Adversarial HTTP tests: only Google's backchannel is simulated; cookies, OIDC and PostgreSQL are real.</summary>
public sealed class GoogleOidcSecurityTests(PostgresFixture postgres)
{
    private const string FlowCookie = "__Host-duora-google-flow";
    private const string LinkConfirmations = "/api/v1/me/external-logins/google/confirmations";

    [Theory]
    [InlineData("state", 0)]
    [InlineData("correlation", 0)]
    [InlineData("nonce", 1)]
    [InlineData("flow", 1)]
    public async Task TamperedBrowserProofCannotValidateAProviderCallback(string proof, int expectedTokenRequests)
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = new Browser(factory);
        var callback = await ChallengeAsync(browser, provider, "Register");
        if (proof == "state")
        {
            var query = QueryHelpers.ParseQuery(new Uri(Browser.BaseAddress, callback).Query);
            callback = QueryHelpers.AddQueryString("/signin-google-oidc", new Dictionary<string, string?>
            {
                ["code"] = query["code"].ToString(),
                ["state"] = Corrupt(query["state"].ToString()),
                ["iss"] = query["iss"].ToString(),
            });
        }
        else
        {
            var name = proof == "flow" ? FlowCookie : browser.Cookies.GetCookies(Browser.BaseAddress)
                .Cast<Cookie>().Single(c => c.Name.StartsWith($"__Host-duora-google-{proof}.", StringComparison.Ordinal)).Name;
            if (proof == "nonce")
            {
                // The protected nonce is in the cookie name; its marker value is not the nonce proof.
                browser.Cookies.Add(Browser.BaseAddress, new Cookie(name, "", "/") { Secure = true, HttpOnly = true, Expired = true });
                ReplaceCookie(browser, Corrupt(name), "N");
            }
            else
            {
                ReplaceCookie(browser, name, Corrupt(browser.Cookie(name)!));
            }
        }

        await CallbackAsync(browser, callback);

        Assert.Equal(expectedTokenRequests, provider.TokenRequests);
        Assert.Equal("Failed", await NextStepAsync(browser));
        Assert.Null(browser.Cookie("__Host-duora"));
        Assert.Null(browser.Cookie("__Host-duora-google-stage"));
        using var registration = await browser.PostJsonAsync("/api/v1/auth/google/registrations", IdentityScenario.RegistrationBody(provider.Email));
        Assert.Equal(HttpStatusCode.BadRequest, registration.StatusCode);
        Assert.Equal(0, await scenario.CountUsersAsync(provider.Email));
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.user_logins where provider_key=$1", provider.Subject));
    }

    [Fact]
    public async Task CallbackReplayCannotRedeemTheCodeAgainOrDestroyTheAlreadyValidatedFlow()
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        using var browser = new Browser(factory);
        var callback = await ChallengeAsync(browser, provider);
        await CallbackAsync(browser, callback);
        Assert.Equal("Unknown", await NextStepAsync(browser));

        await CallbackAsync(browser, callback);

        Assert.Equal(1, provider.TokenRequests);
        Assert.Equal("Unknown", await NextStepAsync(browser));
        Assert.Null(browser.Cookie("__Host-duora"));
        using var completion = await browser.PostJsonAsync("/api/v1/auth/google/completions", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, completion.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingAccountOrSessionBeforeCallbackRejectsTheLink(bool sameAccount)
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        var other = sameAccount ? account : await scenario.CreateConfirmedAccountAsync();
        using var origin = await scenario.SignedInBrowserAsync(account);
        using var replacement = await scenario.SignedInBrowserAsync(other);
        var callback = await ChallengeAsync(origin, provider, "Link");
        Assert.NotEqual(await MfaScenario.ReadSessionIdAsync(origin), await MfaScenario.ReadSessionIdAsync(replacement));
        CopyApplicationCookie(replacement, origin);
        await origin.FetchPrivateContextAsync();

        await CallbackAsync(origin, callback);

        Assert.Equal("Failed", await NextStepAsync(origin));
        using var confirmation = await origin.PostJsonAsync(LinkConfirmations, new { });
        await ProblemAssert.IsProblemAsync(confirmation, HttpStatusCode.BadRequest, "EXTERNAL_FLOW_INVALID");
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.user_logins where provider_key=$1", provider.Subject));
        Assert.Equal(0, await GoogleNoticeCountAsync(scenario, account.UserId));
        Assert.Equal(0, await GoogleNoticeCountAsync(scenario, other.UserId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingAccountOrSessionAfterCallbackRejectsTheLinkConfirmation(bool sameAccount)
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        var other = sameAccount ? account : await scenario.CreateConfirmedAccountAsync();
        using var origin = await scenario.SignedInBrowserAsync(account);
        using var replacement = await scenario.SignedInBrowserAsync(other);
        var callback = await ChallengeAsync(origin, provider, "Link");
        await CallbackAsync(origin, callback);
        Assert.Equal("Link", await NextStepAsync(origin));
        CopyApplicationCookie(replacement, origin);
        await origin.FetchPrivateContextAsync();
        var accountBefore = await AccountStateAsync(scenario, account.UserId);
        var otherBefore = await AccountStateAsync(scenario, other.UserId);

        using var confirmation = await origin.PostJsonAsync(LinkConfirmations, new { });

        await ProblemAssert.IsProblemAsync(confirmation, HttpStatusCode.BadRequest, "EXTERNAL_FLOW_INVALID");
        Assert.Equal(accountBefore, await AccountStateAsync(scenario, account.UserId));
        Assert.Equal(otherBefore, await AccountStateAsync(scenario, other.UserId));
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.user_logins where provider_key=$1", provider.Subject));
    }

    [Fact]
    public async Task GoogleLoginRequiresDuoraMfaBeforeAnyApplicationCookieOrSessionIsIssued()
    {
        using var provider = new GoogleOidcProvider();
        var time = TestClock.StartingNow();
        await using var factory = Enabled(provider, time);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var owner = await scenario.SignedInBrowserAsync(account);
        await LinkAsync(owner, provider);
        var mfa = await MfaScenario.EnableAsync(owner, time);
        using var entrant = new Browser(factory);
        await CallbackAsync(entrant, await ChallengeAsync(entrant, provider));
        var sessionsBefore = await scenario.ScalarAsync("select count(*) from identity.login_sessions where user_id=$1", account.UserId);

        using var missing = await entrant.PostJsonAsync("/api/v1/auth/google/completions", new { });
        await ProblemAssert.IsProblemAsync(missing, HttpStatusCode.Unauthorized, "MFA_REQUIRED");
        Assert.Null(entrant.Cookie("__Host-duora"));
        Assert.Equal(sessionsBefore, await scenario.ScalarAsync("select count(*) from identity.login_sessions where user_id=$1", account.UserId));
        using var wrong = await entrant.PostJsonAsync("/api/v1/auth/google/completions", new { twoFactorCode = Totp.Wrong(time, mfa.SharedKey) });
        await ProblemAssert.IsProblemAsync(wrong, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Null(entrant.Cookie("__Host-duora"));
        Assert.Equal(sessionsBefore, await scenario.ScalarAsync("select count(*) from identity.login_sessions where user_id=$1", account.UserId));

        using var completed = await entrant.PostJsonAsync("/api/v1/auth/google/completions", new { twoFactorCode = Totp.Next(time, mfa.SharedKey) });

        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.NotNull(entrant.Cookie("__Host-duora"));
        var session = await MfaScenario.ReadSessionIdAsync(entrant);
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from identity.login_sessions where id=$1 and user_id=$2 and authentication_method='GoogleOidc' and mfa_verified_at is not null and totp_verified_at is not null and reauthenticated_at is null and revoked_at is null",
            session, account.UserId));
    }

    [Fact]
    public async Task TwoAccountsRacingToLinkTheSameGoogleSubjectHaveExactlyOneWinner()
    {
        using var provider = new GoogleOidcProvider();
        var barrier = new LinkInsertBarrier(provider.Subject);
        await using var factory = Enabled(provider, interceptor: barrier);
        var scenario = new IdentityScenario(postgres, factory);
        var first = await scenario.CreateConfirmedAccountAsync();
        var second = await scenario.CreateConfirmedAccountAsync();
        using var firstBrowser = await scenario.SignedInBrowserAsync(first);
        using var secondBrowser = await scenario.SignedInBrowserAsync(second);
        await CallbackAsync(firstBrowser, await ChallengeAsync(firstBrowser, provider, "Link"));
        await CallbackAsync(secondBrowser, await ChallengeAsync(secondBrowser, provider, "Link"));
        var firstBefore = await AccountStateAsync(scenario, first.UserId);
        var secondBefore = await AccountStateAsync(scenario, second.UserId);

        // Both transactions must reach their INSERT before either can write: scheduling alone is not a race proof.
        var responses = await Task.WhenAll(firstBrowser.PostJsonAsync(LinkConfirmations, new { }), secondBrowser.PostJsonAsync(LinkConfirmations, new { }));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        Assert.Equal(2, barrier.Arrivals);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var firstWon = firstResponse.StatusCode == HttpStatusCode.NoContent;
        var winner = firstWon ? first : second;
        var loser = firstWon ? second : first;
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.user_logins where provider_key=$1", provider.Subject));
        Assert.Equal(winner.UserId.ToString("D"), await scenario.ScalarTextAsync("select user_id::text from identity.user_logins where provider_key=$1", provider.Subject));
        Assert.Equal(firstWon ? secondBefore : firstBefore, await AccountStateAsync(scenario, loser.UserId));
        Assert.Equal(1, await GoogleNoticeCountAsync(scenario, winner.UserId));
        Assert.Equal(0, await GoogleNoticeCountAsync(scenario, loser.UserId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLinkOrUnlinkNoticeRollsBackTheCredentialStampSessionsFlowAndAudit(bool unlink)
    {
        using var provider = new GoogleOidcProvider();
        var failure = new FailAfterGoogleNotice();
        await using var factory = Enabled(provider, interceptor: failure);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var owner = await scenario.SignedInBrowserAsync(account);
        if (unlink)
        {
            await LinkAsync(owner, provider);
        }
        else
        {
            await CallbackAsync(owner, await ChallengeAsync(owner, provider, "Link"));
        }

        var before = await AccountStateAsync(scenario, account.UserId);
        failure.Arm(account.UserId);
        using var failed = unlink ? await owner.DeleteAsync("/api/v1/me/external-logins/google") : await owner.PostJsonAsync(LinkConfirmations, new { });

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(failure.Fired);
        Assert.Equal(before, await AccountStateAsync(scenario, account.UserId));
        using var retry = unlink ? await owner.DeleteAsync("/api/v1/me/external-logins/google") : await owner.PostJsonAsync(LinkConfirmations, new { });
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(unlink ? 0 : 1, await scenario.ScalarAsync("select count(*) from identity.user_logins where user_id=$1", account.UserId));
        Assert.Equal(unlink ? 2 : 1, await GoogleNoticeCountAsync(scenario, account.UserId));
    }

    private DuoraWebFactory Enabled(GoogleOidcProvider provider, FakeTimeProvider? time = null, SaveChangesInterceptor? interceptor = null) => new(postgres.ConnectionString)
    {
        Time = time,
        Settings = new Dictionary<string, string?>
        {
            ["GoogleOidc:Enabled"] = "true",
            ["GoogleOidc:RegistrationEnabled"] = "true",
            ["GoogleOidc:LinkingEnabled"] = "true",
            ["GoogleOidc:ClientId"] = "synthetic-duora-client",
            ["GoogleOidc:ClientSecret"] = "synthetic-duora-secret",
        },
        TestServices = services =>
        {
            services.Configure<OpenIdConnectOptions>("Duora.GoogleOidc", options => options.Backchannel = new HttpClient(provider, disposeHandler: false));
            if (interceptor is not null)
            {
                services.ConfigureDbContext<DuoraDbContext>(options => options.AddInterceptors(interceptor));
            }
        },
    };

    private static async Task<string> ChallengeAsync(Browser browser, GoogleOidcProvider provider, string purpose = "Login")
    {
        await browser.FetchCsrfTokenAsync();
        using var start = await browser.PostJsonAsync(purpose == "Link" ? "/api/v1/me/external-logins/google/requests" : "/api/v1/auth/google/requests", new { purpose });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var body = JsonDocument.Parse(await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("/api/v1/auth/google/authorize", body.RootElement.GetProperty("authorizationUrl").GetString());
        using var challenge = await browser.GetAsync("/api/v1/auth/google/authorize");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        return provider.Callback(challenge.Headers.Location!);
    }

    private static async Task CallbackAsync(Browser browser, string callback)
    {
        using var response = await browser.GetAsync(callback);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/google/continue", response.Headers.Location!.OriginalString);
        await browser.FetchCsrfTokenAsync();
    }

    private static async Task LinkAsync(Browser browser, GoogleOidcProvider provider)
    {
        await CallbackAsync(browser, await ChallengeAsync(browser, provider, "Link"));
        using var linked = await browser.PostJsonAsync(LinkConfirmations, new { });
        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        await browser.FetchPrivateContextAsync();
    }

    private static async Task<string> NextStepAsync(Browser browser)
    {
        using var response = await browser.GetAsync("/api/v1/auth/google/flow");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("nextStep").GetString()!;
    }

    private static string Corrupt(string value)
    {
        var middle = value.Length / 2;
        return value[..middle] + (value[middle] == 'A' ? 'B' : 'A') + value[(middle + 1)..];
    }

    private static void ReplaceCookie(Browser browser, string name, string value) =>
        browser.Cookies.Add(Browser.BaseAddress, new Cookie(name, value, "/") { Secure = true, HttpOnly = true });

    private static void CopyApplicationCookie(Browser source, Browser target)
    {
        Assert.NotNull(source.Cookie("__Host-duora"));
        foreach (Cookie cookie in source.Cookies.GetCookies(Browser.BaseAddress))
        {
            if (cookie.Name == "__Host-duora" || cookie.Name.StartsWith("__Host-duoraC", StringComparison.Ordinal))
            {
                ReplaceCookie(target, cookie.Name, cookie.Value);
            }
        }
    }

    private static Task<long> GoogleNoticeCountAsync(IdentityScenario scenario, Guid userId) => scenario.ScalarAsync(
        "select count(*) from ops.outbox_events where payload->>'userId'=$1::text and payload->>'kind' in ('GoogleLinkedNotice','GoogleUnlinkedNotice')", userId);

    private static Task<string> AccountStateAsync(IdentityScenario scenario, Guid userId) => scenario.ScalarTextAsync(
        """
        select concat_ws('|', u.password_hash, u.security_stamp, u.concurrency_stamp,
            (select coalesce(string_agg(concat_ws(':', s.id, s.security_stamp, s.revoked_at, s.revocation_reason), ',' order by s.id), '') from identity.login_sessions s where s.user_id=u.id),
            (select coalesce(string_agg(l.provider_key, ',' order by l.provider_key), '') from identity.user_logins l where l.user_id=u.id),
            (select coalesce(string_agg(concat_ws(':', f.id, f.stage, f.protected_proof), ',' order by f.id), '') from identity.external_auth_flows f where f.user_id=u.id),
            (select count(*) from ops.audit_events a where a.target_id=u.id),
            (select count(*) from ops.outbox_events e where e.payload->>'userId'=u.id::text))
        from identity.users u where u.id=$1
        """, userId);

    private sealed class LinkInsertBarrier(string subject) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<IdentityUserLogin<Guid>>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.ProviderKey == subject) == true)
            {
                if (Interlocked.Increment(ref _arrivals) == 2)
                {
                    _bothArrived.TrySetResult();
                }

                await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }

            return result;
        }
    }

    private sealed class FailAfterGoogleNotice : SaveChangesInterceptor
    {
        private Guid? _userId;
        private bool _noticePending;
        public bool Fired { get; private set; }
        public void Arm(Guid userId) => _userId = userId;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            _noticePending = _userId is { } userId && !Fired && eventData.Context is { } context
                && context.ChangeTracker.Entries<OutboxEvent>().Any(entry => entry.State == EntityState.Added && IsGoogleNotice(entry.Entity, userId));
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (_noticePending)
            {
                _noticePending = false;
                Fired = true;
                throw new InvalidOperationException("Synthetic failure after persisting the Google credential notice.");
            }

            return ValueTask.FromResult(result);
        }

        private static bool IsGoogleNotice(OutboxEvent entry, Guid userId)
        {
            using var payload = JsonDocument.Parse(entry.Payload);
            return entry.ResourceId == userId && payload.RootElement.GetProperty("kind").GetString() is "GoogleLinkedNotice" or "GoogleUnlinkedNotice";
        }
    }
}
