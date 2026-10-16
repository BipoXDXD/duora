using System.Net;
using Duora.Core.Identity;
using Duora.Web.Admin;
using Duora.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Rotas <c>/admin</c> (TM-18; ADR-0017): enumeradas a partir das rotas registradas, cada uma nega quem não tem a
/// sessão administrativa completa. O grupo ainda não tem rotas de produto; o teste pendura sondas pelo mesmo
/// <see cref="AdminRoutes"/> que os módulos usarão, então o que é testado é a proteção do grupo, não da sonda.
/// </summary>
public sealed class AdminRouteTests : IAsyncDisposable
{
    private const string AdminPrefix = "/api/v1/admin";
    private const string Probe = AdminPrefix + "/probe";
    private const string CriticalProbe = AdminPrefix + "/probe-critical";

    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time;
    private readonly DuoraWebFactory _factory;

    public AdminRouteTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = _time,
            TestServices = services => services.AddSingleton(new AdminRoutes(admin =>
            {
                admin.MapGet("/probe", () => TypedResults.NoContent());
                admin.MapPost("/probe-critical", () => TypedResults.NoContent()).RequireRecentMfa();
            })),
        };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void EveryAdminRouteCarriesTheAdministrativeSessionPolicy()
    {
        var routes = AdminEndpointsOf(_factory);

        Assert.NotEmpty(routes);
        Assert.All(routes, endpoint =>
        {
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Contains(
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                data => data.Policy == SessionAuthorization.AdministrativeSession);
        });
    }

    [Fact]
    public async Task ProductionAdminRoutesCarryTheAdministrativeSessionPolicyToo()
    {
        await using var production = new DuoraWebFactory(_postgres.ConnectionString);

        Assert.All(AdminEndpointsOf(production), endpoint => Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            data => data.Policy == SessionAuthorization.AdministrativeSession));
    }

    [Fact]
    public async Task WithoutASessionEveryAdminRouteIs401()
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        foreach (var route in AdminRoutesOf(_factory))
        {
            using var response = await SendAsync(browser, route);
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        }
    }

    [Fact]
    public async Task AParticipantSessionIsForbiddenOnEveryAdminRoute()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        await Scenario.GrantRoleAsync(account.UserId, AccountRoles.Participant);
        using var browser = await Scenario.SignedInBrowserAsync(account);

        await AssertEveryAdminRouteAsync(browser, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task AParticipantWithMfaIsStillForbiddenOnEveryAdminRoute()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser);

        await AssertEveryAdminRouteAsync(browser, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Theory]
    [InlineData(AccountRoles.Moderator)]
    [InlineData(AccountRoles.ProductAdmin)]
    [InlineData(AccountRoles.Operator)]
    public async Task APrivilegedSessionWithoutMfaMustEnrollFirst(string role)
    {
        var account = await Scenario.CreatePrivilegedAccountAsync(role);
        using var browser = await Scenario.SignedInBrowserAsync(account);

        await AssertEveryAdminRouteAsync(browser, HttpStatusCode.Forbidden, "MFA_ENROLLMENT_REQUIRED");
    }

    [Fact]
    public async Task APrivilegedSessionWithMfaReachesTheAdminRoutes()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser);

        foreach (var route in AdminRoutesOf(_factory))
        {
            using var response = await SendAsync(browser, route);
            Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{route.Name} respondeu {(int)response.StatusCode}.");
        }
    }

    [Fact]
    public async Task APrivilegedLoginWithTheSecondFactorOpensACompleteAdministrativeSession()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        EnabledMfa mfa;
        using (var enrolling = await Scenario.SignedInBrowserAsync(account))
        {
            mfa = await MfaScenario.EnableAsync(enrolling);
        }

        using var browser = new Browser(_factory);
        using var login = await MfaScenario.LoginAsync(browser, account, twoFactorCode: Totp.Now(mfa.SharedKey));
        using var probe = await browser.GetAsync(Probe);

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, probe.StatusCode);
    }

    [Fact]
    public async Task ACriticalActionNeedsASecondFactorProvedInTheLastFiveMinutes()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var mfa = await MfaScenario.EnableAsync(browser);
        _time.Advance(TimeSpan.FromMinutes(5));

        using var stale = await browser.PostJsonAsync(CriticalProbe, body: null);
        using var routine = await browser.GetAsync(Probe);
        using var passwordOnly = await browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password });
        using var stillStale = await browser.PostJsonAsync(CriticalProbe, body: null);
        using var stepUp = await browser.PostJsonAsync(
            MfaScenario.Reauthenticate,
            new { password = account.Password, twoFactorCode = Totp.Now(mfa.SharedKey) });
        using var fresh = await browser.PostJsonAsync(CriticalProbe, body: null);

        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        Assert.Equal(HttpStatusCode.NoContent, routine.StatusCode);
        await ProblemAssert.IsProblemAsync(passwordOnly, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.Unauthorized, stillStale.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, stepUp.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
    }

    [Fact]
    public async Task ARoleGrantedDuringANormalSessionNeedsANewAdministrativeLogin()
    {
        // A sessão normal tem a janela de 30 min/12 h; só um login novo abre a administrativa (15 min/8 h).
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser);
        await Scenario.GrantRoleAsync(account.UserId, AccountRoles.Moderator);

        using var probe = await browser.GetAsync(Probe);

        await ProblemAssert.IsProblemAsync(probe, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task RemovingThePrivilegedRoleClosesTheAdminRoutesOnTheNextRequest()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser);

        await Scenario.ScalarAsync(
            "with removed as (delete from identity.user_roles where user_id = $1 returning 1) select count(*) from removed",
            account.UserId);
        using var probe = await browser.GetAsync(Probe);

        await ProblemAssert.IsProblemAsync(probe, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    private async Task AssertEveryAdminRouteAsync(Browser browser, HttpStatusCode status, string code)
    {
        var routes = AdminRoutesOf(_factory);
        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            using var response = await SendAsync(browser, route);
            Assert.True(response.StatusCode == status, $"{route.Name} respondeu {(int)response.StatusCode}.");
            await ProblemAssert.IsProblemAsync(response, status, code);
        }
    }

    private static List<RouteEndpoint> AdminEndpointsOf(DuoraWebFactory factory) =>
    [
        .. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => ("/" + (endpoint.RoutePattern.RawText ?? "").Trim('/'))
                .StartsWith(AdminPrefix + "/", StringComparison.Ordinal)),
    ];

    private static List<(string Name, string Method, string Path)> AdminRoutesOf(DuoraWebFactory factory) =>
    [
        .. AdminEndpointsOf(factory).SelectMany(endpoint =>
            (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(method =>
            {
                var path = "/" + (endpoint.RoutePattern.RawText ?? "").Trim('/');
                return (Name: $"{method} {path}", Method: method, Path: path);
            })),
    ];

    private static Task<HttpResponseMessage> SendAsync(Browser browser, (string Name, string Method, string Path) route)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(route.Path, "{[^}]+}", Guid.NewGuid().ToString());
        return route.Method switch
        {
            "GET" => browser.GetAsync(path),
            "DELETE" => browser.DeleteAsync(path),
            "POST" => browser.PostJsonAsync(path, body: null),
            var method => throw new InvalidOperationException($"Método {method} sem caso no teste."),
        };
    }
}
