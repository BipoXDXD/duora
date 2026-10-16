using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// MFA obrigatório da conta privilegiada (ADR-0017): sem segundo fator, a sessão só serve para cadastrá-lo; o MFA
/// não sai pela API-A15; e o reset de senha não o contorna (TM-22).
/// </summary>
public sealed class AdminMfaTests : IAsyncDisposable
{
    /// <summary>Allowlist da sessão restrita (auth.md): o que é preciso para cadastrar o MFA.</summary>
    private static readonly string[] EnrollmentRoutes =
    [
        "GET /api/v1/me",
        "POST /api/v1/me/mfa/enrollments",
        "POST /api/v1/me/mfa/confirmations",
        "POST /api/v1/auth/reauthenticate",
    ];

    private readonly PostgresFixture _postgres;

    private const string NewPassword = "uma frase nova e longa";

    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time;
    private readonly DuoraWebFactory _factory;

    public AdminMfaTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _time = TestClock.StartingNow();
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
    }

    private IdentityScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void TheEnrollmentAllowlistIsExactlyTheRoutesMarkedForIt()
    {
        var marked = Routes()
            .Where(route => route.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(route => route.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => data.Policy == Duora.Web.Security.SessionAuthorization.SignedIn))
            .Select(route => route.Name);

        Assert.Equal(EnrollmentRoutes.Order(StringComparer.Ordinal), marked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task APrivilegedSessionWithoutMfaIsDeniedEverywhereElse()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var protectedRoutes = Routes()
            .Where(route => route.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(route => !EnrollmentRoutes.Contains(route.Name))
            .ToList();

        Assert.NotEmpty(protectedRoutes);
        foreach (var route in protectedRoutes)
        {
            var path = System.Text.RegularExpressions.Regex.Replace(route.Path, "{[^}]+}", Guid.NewGuid().ToString());
            using var response = route.Method switch
            {
                "GET" => await browser.GetAsync(path),
                "DELETE" => await browser.DeleteAsync(path),
                "POST" => await browser.PostJsonAsync(path, new { }),
                "PUT" => await browser.PutAsync(path, "{}"),
                "PATCH" => await browser.PatchRawJsonAsync(path, "{}", ifMatch: "\"1\""),
                var method => throw new InvalidOperationException($"Método {method} sem caso no teste."),
            };
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{route.Name} respondeu {(int)response.StatusCode}.");
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "MFA_ENROLLMENT_REQUIRED");
        }

        using var me = await browser.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task EnrollingCompletesTheAdministrativeSession()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        await MfaScenario.EnableAsync(browser, _time);
        using var sessions = await browser.GetAsync("/api/v1/me/sessions");

        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
        var active = Assert.Single(await Scenario.SessionRowsAsync(account.UserId), row => !row.Revoked);
        Assert.Equal("Administrative", active.Policy);
        Assert.NotNull(active.MfaVerifiedAt);
    }

    [Fact]
    public async Task APrivilegedAccountCannotDisableItsMfa()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, _time);

        using var response = await browser.DeleteAsync(MfaScenario.Mfa);
        using var me = await browser.GetAsync("/api/v1/me");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "MFA_REQUIRED_BY_ROLE");
        Assert.True((bool)(await MfaScenario.ReadObjectAsync(me))["mfaEnabled"]!);
    }

    [Fact]
    [Trait("Threat", "TM-22")]
    public async Task PasswordResetDoesNotBypassMfaForPrivilegedAccount()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        EnabledMfa mfa;
        using (var enrolling = await Scenario.SignedInBrowserAsync(account))
        {
            mfa = await MfaScenario.EnableAsync(enrolling, _time);
        }

        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        using (await browser.PostJsonAsync("/api/v1/auth/password-reset-requests", new { email = account.Email }))
        {
        }

        var link = await Scenario.ReadLatestLinkAsync(account.Email, "PasswordReset");
        using var reset = await browser.PostJsonAsync(
            "/api/v1/auth/password-resets",
            new { userId = link.UserId, token = link.Token, newPassword = NewPassword });
        var renewed = account with { Password = NewPassword };
        using var withoutCode = await MfaScenario.LoginAsync(browser, renewed);
        using var withCode = await MfaScenario.LoginAsync(browser, renewed, twoFactorCode: Totp.Next(_time, mfa.SharedKey));

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        await ProblemAssert.IsProblemAsync(withoutCode, HttpStatusCode.Unauthorized, "MFA_REQUIRED");
        Assert.Equal(HttpStatusCode.NoContent, withCode.StatusCode);
    }

    private IEnumerable<(string Name, string Method, string Path, RouteEndpoint Endpoint)> Routes()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var path = "/" + (endpoint.RoutePattern.RawText ?? "").Trim('/');
            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                yield return ($"{method} {path}", method, path, endpoint);
            }
        }
    }
}
