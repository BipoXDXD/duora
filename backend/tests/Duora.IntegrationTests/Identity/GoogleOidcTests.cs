extern alias worker;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

public sealed class GoogleOidcTests(PostgresFixture postgres)
{
    [Fact]
    public async Task DisabledProviderIsDiscoverableWithoutCredentialsAndCannotStart()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        using var browser = new Browser(factory);
        using var options = await browser.GetAsync("/api/v1/auth/google/options");
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        using var json = JsonDocument.Parse(await options.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
        await browser.FetchCsrfTokenAsync();
        using var start = await browser.PostJsonAsync("/api/v1/auth/google/requests", new { purpose = "Login" });
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    private DuoraWebFactory Enabled(GoogleOidcProvider provider, FakeTimeProvider? time = null) => new(postgres.ConnectionString)
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
        TestServices = services => services.Configure<OpenIdConnectOptions>("Duora.GoogleOidc", options =>
            options.Backchannel = new HttpClient(provider, disposeHandler: false)),
    };

    private static async Task<string> AuthenticateAsync(Browser browser, GoogleOidcProvider provider, string purpose = "Login")
    {
        await browser.FetchCsrfTokenAsync();
        using var start = await browser.PostJsonAsync(purpose == "Link" ? "/api/v1/me/external-logins/google/requests" : "/api/v1/auth/google/requests", new { purpose });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var body = JsonDocument.Parse(await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var target = body.RootElement.GetProperty("authorizationUrl").GetString();
        Assert.Equal("/api/v1/auth/google/authorize", target);
        using var challenge = await browser.GetAsync(target!);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var callback = provider.Callback(challenge.Headers.Location!);
        using var result = await browser.GetAsync(callback);
        Assert.Equal(HttpStatusCode.SeeOther, result.StatusCode);
        Assert.Equal("/google/continue", result.Headers.Location!.OriginalString);
        await browser.FetchCsrfTokenAsync();
        return callback;
    }

    private static async Task<string> NextStepAsync(Browser browser)
    {
        using var response = await browser.GetAsync("/api/v1/auth/google/flow");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.GetProperty("nextStep").GetString()!;
    }

    [Fact]
    public async Task VerifiedGoogleEmailDoesNotTakeOverAnExistingLocalAccount()
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        provider.Email = account.Email;
        using var browser = new Browser(factory);
        await AuthenticateAsync(browser, provider);
        Assert.Equal("Unknown", await NextStepAsync(browser));
        using var complete = await browser.PostJsonAsync("/api/v1/auth/google/completions", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
        Assert.Null(browser.Cookie("__Host-duora"));
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.user_logins where user_id=$1", account.UserId));
    }

    [Fact]
    public async Task ExplicitLinkAllowsGoogleLoginButNeverGrantsLocalPasswordStepUp()
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var owner = await scenario.SignedInBrowserAsync(account);
        await AuthenticateAsync(owner, provider, "Link");
        Assert.Equal("Link", await NextStepAsync(owner));
        using var linked = await owner.PostJsonAsync("/api/v1/me/external-logins/google/confirmations", new { });
        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.user_logins where user_id=$1 and provider_key=$2", account.UserId, provider.Subject));
        using var entrant = new Browser(factory);
        await AuthenticateAsync(entrant, provider);
        using var completed = await entrant.PostJsonAsync("/api/v1/auth/google/completions", new { });
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        await entrant.FetchPrivateContextAsync();
        using var stepUp = await entrant.PostJsonAsync(MfaScenario.Enrollments, body: null);
        Assert.Equal(HttpStatusCode.Unauthorized, stepUp.StatusCode);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.login_sessions where user_id=$1 and authentication_method='GoogleOidc' and revoked_at is null and reauthenticated_at is not null", account.UserId));
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("email")]
    public async Task InvalidProviderProofCannotBecomeAnApplicationSession(string corruption)
    {
        using var provider = new GoogleOidcProvider();
        switch (corruption)
        {
            case "nonce":
                provider.WrongNonce = true;
                break;
            case "issuer":
                provider.Issuer = "https://evil.example";
                break;
            case "audience":
                provider.Audience = "other-client";
                break;
            case "signature":
                provider.WrongSignature = true;
                break;
            case "expired":
                provider.Expired = true;
                break;
            case "email":
                provider.VerifiedEmail = false;
                break;
        }
        await using var factory = Enabled(provider);
        using var browser = new Browser(factory);
        await AuthenticateAsync(browser, provider);
        Assert.Equal("Failed", await NextStepAsync(browser));
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    [Fact]
    public async Task RegistrationNeedsDuoraPasswordAndCodeBeforeCreatingTheExternalLogin()
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = new Browser(factory);
        await AuthenticateAsync(browser, provider, "Register");
        using var registered = await browser.PostJsonAsync("/api/v1/auth/google/registrations", IdentityScenario.RegistrationBody(provider.Email));
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var proof = await scenario.ReadLatestLinkAsync(provider.Email, "EmailConfirmation");
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.user_logins where user_id=$1", proof.UserId));
        using var confirmed = await browser.PostJsonAsync("/api/v1/auth/google/registration-confirmations", new { code = proof.Token, password = IdentityScenario.ValidPassword });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.user_logins where user_id=$1", proof.UserId));
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    [Fact]
    public async Task LocalReregistrationInvalidatesPendingGoogleAssociation()
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = new Browser(factory);
        await AuthenticateAsync(browser, provider, "Register");
        using var registered = await browser.PostJsonAsync("/api/v1/auth/google/registrations", IdentityScenario.RegistrationBody(provider.Email));
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        using var replacement = await IdentityScenario.RegisterAsync(browser, provider.Email, "different secure password");
        Assert.Equal(HttpStatusCode.Accepted, replacement.StatusCode);
        var proof = await scenario.ReadLatestLinkAsync(provider.Email, "EmailConfirmation");
        using var confirmed = await browser.PostJsonAsync("/api/v1/auth/google/registration-confirmations", new { code = proof.Token, password = "different secure password" });
        Assert.Equal(HttpStatusCode.BadRequest, confirmed.StatusCode);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.user_logins where user_id=$1", proof.UserId));
    }

    [Fact]
    public async Task UnlinkInvalidatesAlreadyValidatedGoogleLogin()
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var owner = await scenario.SignedInBrowserAsync(account);
        await AuthenticateAsync(owner, provider, "Link");
        using var linked = await owner.PostJsonAsync("/api/v1/me/external-logins/google/confirmations", new { });
        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        await owner.FetchPrivateContextAsync();
        using var entrant = new Browser(factory);
        await AuthenticateAsync(entrant, provider);
        using var unlinked = await owner.DeleteAsync("/api/v1/me/external-logins/google");
        Assert.Equal(HttpStatusCode.NoContent, unlinked.StatusCode);
        using var completed = await entrant.PostJsonAsync("/api/v1/auth/google/completions", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, completed.StatusCode);
        Assert.Null(entrant.Cookie("__Host-duora"));
    }

    [Fact]
    public async Task ExpiredProofIsPurgedWithoutProviderOrNewAuthenticationRequests()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var expired = Guid.NewGuid();
        var live = Guid.NewGuid();
        await scenario.ScalarAsync("insert into identity.external_auth_flows(id,browser_hash,purpose,stage,protected_proof,created_at,expires_at,attempts) values($1,decode(repeat('00',32),'hex'),'Login','Validated','synthetic expired encrypted proof',now()-interval '10 minutes',now()-interval '5 minutes',0) returning 1", expired);
        await scenario.ScalarAsync("insert into identity.external_auth_flows(id,browser_hash,purpose,stage,created_at,expires_at,attempts) values($1,decode(repeat('00',32),'hex'),'Login','Created',now(),now()+interval '5 minutes',0) returning 1", live);
        var retention = factory.Services.GetRequiredService<Duora.Infrastructure.Identity.ExternalAuthFlowRetention>();
        Assert.True(await retention.PurgeExpiredAsync(TestContext.Current.CancellationToken) >= 1);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.external_auth_flows where id=$1", expired));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.external_auth_flows where id=$1", live));
    }

    [Fact]
    public async Task FlowTimestampsMatchTheFrontendUtcZContract()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        using var browser = new Browser(factory);
        using var response = await browser.GetAsync("/api/v1/auth/google/flow");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.EndsWith("Z", body.RootElement.GetProperty("expiresAt").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("&id_token=unexpected")]
    [InlineData("&state=duplicate")]
    [InlineData("&code=duplicate")]
    [InlineData("&iss=https%3A%2F%2Fevil.example")]
    public async Task AmbiguousOrImplicitCallbackIsRejectedBeforeCodeRedemption(string extra)
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        using var start = await browser.PostJsonAsync("/api/v1/auth/google/requests", new { purpose = "Login" });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var challenge = await browser.GetAsync("/api/v1/auth/google/authorize");
        var callback = provider.Callback(challenge.Headers.Location!);
        using var response = await browser.GetAsync(callback + extra);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, provider.TokenRequests);
        Assert.Null(browser.Cookie("__Host-duora"));
    }

    [Fact]
    public async Task PreviousAnonymousGoogleEmailIsNotDisclosedAfterLocalIdentityChanges()
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = new Browser(factory);
        await AuthenticateAsync(browser, provider, "Register");
        Assert.Equal("Register", await NextStepAsync(browser));
        using var login = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using var response = await browser.GetAsync("/api/v1/auth/google/flow");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Failed", body.RootElement.GetProperty("nextStep").GetString());
        Assert.True(!body.RootElement.TryGetProperty("email", out var email) || email.ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData("/signout-oidc")]
    [InlineData("/signout-callback-oidc")]
    public async Task UnusedOidcSignoutPathsCannotBypassNormalAuthorization(string path)
    {
        using var provider = new GoogleOidcProvider();
        await using var factory = Enabled(provider);
        using var browser = new Browser(factory);
        using var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisteredWorkerPurgesExpiredProofWithGoogleOffAndNoWebProtector()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var expired = Guid.NewGuid();
        await scenario.ScalarAsync("insert into identity.external_auth_flows(id,browser_hash,purpose,stage,protected_proof,created_at,expires_at,attempts) values($1,decode(repeat('00',32),'hex'),'Login','Validated','expired synthetic proof',now()-interval '10 minutes',now()-interval '5 minutes',0) returning 1", expired);
        using var host = worker::Duora.Worker.WorkerHost.CreateBuilder(
        [
            "--environment", "Testing", $"--ConnectionStrings:Duora={postgres.ConnectionString}",
            "--Duora:PublicOrigin=https://duora.test", $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-google-worker-unused")}",
            "--GoogleOidc:Enabled=false",
        ]).Build();
        Assert.Null(host.Services.GetService<Duora.Infrastructure.Identity.IExternalIdentityProtection>());
        var cleanup = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<worker::Duora.Worker.ExternalAuthFlowRetentionService>().Single();
        await cleanup.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (await scenario.ScalarAsync("select count(*) from identity.external_auth_flows where id=$1", expired) != 0 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.external_auth_flows where id=$1", expired));
        }
        finally
        {
            await cleanup.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
