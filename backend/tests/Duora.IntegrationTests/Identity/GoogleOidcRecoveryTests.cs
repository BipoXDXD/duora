using System.Net;
using System.Text.Json;
using Duora.Core.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>Removing a stored credential is local recovery, not a new provider authentication or association.</summary>
public sealed class GoogleOidcRecoveryTests(PostgresFixture postgres)
{
    private const string Logins = "/api/v1/me/external-logins";
    private const string Google = Logins + "/google";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LocalOwnerCanListAndRemoveWhenNewGoogleAssociationsAreDisabled(bool enabled, bool suspended)
    {
        using var network = new NoProviderNetwork();
        await using var factory = Factory(network, enabled, linking: false);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SeedLinkAsync(scenario, account.UserId);
        if (suspended)
        {
            await scenario.ScalarAsync("update identity.users set status='Suspended' where id=$1 returning 1", account.UserId);
        }
        using var owner = await scenario.SignedInBrowserAsync(account);
        using var listed = await owner.GetAsync(Logins);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        using var removed = await owner.DeleteAsync(Google);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.user_logins where user_id=$1", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from ops.outbox_events where payload->>'userId'=$1::text and payload->>'kind'='GoogleUnlinkedNotice'", account.UserId));
        await owner.FetchPrivateContextAsync();
        using var prohibited = await owner.PostJsonAsync(Google + "/requests", new { });
        Assert.Equal(suspended ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, prohibited.StatusCode);
        Assert.Equal(0, network.Requests);
    }

    [Theory]
    [InlineData(AccountRoles.Moderator)]
    [InlineData(AccountRoles.ProductAdmin)]
    [InlineData(AccountRoles.Operator)]
    public async Task FormerParticipantCanRemoveGoogleAfterRoleChangeButCannotCreateANewLink(string role)
    {
        using var network = new NoProviderNetwork();
        var time = TestClock.StartingNow();
        await using var factory = Factory(network, enabled: true, linking: true, time);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SeedLinkAsync(scenario, account.UserId);
        using var owner = await scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(owner, time);
        await scenario.ScalarAsync("delete from identity.user_roles where user_id=$1 returning 1", account.UserId);
        await scenario.GrantRoleAsync(account.UserId, role);
        await owner.FetchPrivateContextAsync();
        using var listed = await owner.GetAsync(Logins);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var removed = await owner.DeleteAsync(Google);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.user_logins where user_id=$1", account.UserId));
        await owner.FetchPrivateContextAsync();
        using var link = await owner.PostJsonAsync(Google + "/requests", new { });
        await ProblemAssert.IsProblemAsync(link, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(0, network.Requests);
    }

    [Fact]
    public async Task OfflineRecoveryStillRequiresLocalRecentAuthenticationAndPrivateCsrf()
    {
        using var network = new NoProviderNetwork();
        await using var factory = Factory(network, enabled: false, linking: false);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        await SeedLinkAsync(scenario, account.UserId);
        using var owner = await scenario.SignedInBrowserAsync(account);
        using var noContext = await owner.GetWithoutContextAsync(Logins);
        await ProblemAssert.IsProblemAsync(noContext, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        using var noCsrf = await owner.DeleteAsync(Google, new MutationHeaders(SendCsrfToken: false));
        await ProblemAssert.IsProblemAsync(noCsrf, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        var session = await MfaScenario.ReadSessionIdAsync(owner);
        await scenario.ScalarAsync("update identity.login_sessions set reauthenticated_at=now()-interval '61 minutes' where id=$1 returning 1", session);
        using var expired = await owner.DeleteAsync(Google);
        await ProblemAssert.IsProblemAsync(expired, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.user_logins where user_id=$1", account.UserId));
        Assert.Equal(0, network.Requests);
    }

    private DuoraWebFactory Factory(NoProviderNetwork network, bool enabled, bool linking, TimeProvider? time = null) => new(postgres.ConnectionString)
    {
        Time = time,
        Settings = new Dictionary<string, string?>
        {
            ["GoogleOidc:Enabled"] = enabled.ToString(),
            ["GoogleOidc:LinkingEnabled"] = linking.ToString(),
            ["GoogleOidc:ClientId"] = enabled ? "synthetic-recovery-client" : null,
            ["GoogleOidc:ClientSecret"] = enabled ? "synthetic-recovery-secret" : null,
        },
        TestServices = services => services.Configure<OpenIdConnectOptions>("Duora.GoogleOidc", options =>
            options.Backchannel = new HttpClient(network, disposeHandler: false)),
    };

    private static Task<long> SeedLinkAsync(IdentityScenario scenario, Guid userId) => scenario.ScalarAsync(
        "insert into identity.user_logins(login_provider,provider_key,provider_display_name,user_id) values('google-oidc',$1,'Google',$2) returning 1",
        $"synthetic-recovery-{Guid.NewGuid():N}", userId);

    private sealed class NoProviderNetwork : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("Local credential removal must not use the provider network.");
        }
    }
}
