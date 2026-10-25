using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>HTTP real: a intenção/prova de uma sessão não pode ler/escrever sob outro cookie, mesmo sem Broadcast.</summary>
public sealed class PrivateOperationContextTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    private IdentityScenario Scenario => new(postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task BootstrapPairsIdentitySessionAndProofWithoutReplacingThePublicCookie()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var publicToken = browser.Cookie("XSRF-TOKEN");

        var binding = await BootstrapAsync(browser);

        Assert.Equal(account.UserId, binding.UserId);
        Assert.NotEqual(Guid.Empty, binding.SessionId);
        Assert.Equal("active", binding.Phase);
        Assert.NotEmpty(binding.Proof);
        Assert.Equal(publicToken, browser.Cookie("XSRF-TOKEN"));
        using var response = await RawAsync(browser, HttpMethod.Get, "/api/v1/me/time-zone", binding.Proof);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task CookieChangedToAnotherPersonRejectsOldReadAndWriteWithoutChangingTheirPreference()
    {
        var accountA = await Scenario.CreateConfirmedAccountAsync();
        var accountB = await Scenario.CreateConfirmedAccountAsync();
        using var browserA = await Scenario.SignedInBrowserAsync(accountA);
        using var browserB = await Scenario.SignedInBrowserAsync(accountB);
        var original = await BootstrapAsync(browserA);
        var before = await ReadTimeZoneAsync(accountB.UserId);
        CopySessionCookie(browserB, browserA);

        using var read = await RawAsync(browserA, HttpMethod.Get, "/api/v1/me/time-zone", original.Proof);
        using var write = await RawAsync(browserA, HttpMethod.Put, "/api/v1/me/time-zone", original.Proof,
            new { timeZone = "America/Sao_Paulo" }, $"\"{before.Version}\"");

        await ProblemAssert.IsProblemAsync(read, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        await ProblemAssert.IsProblemAsync(write, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        Assert.Equal(before, await ReadTimeZoneAsync(accountB.UserId));
    }

    [Fact]
    public async Task SamePersonWithAnotherSessionRejectsTheOldProofUntilExplicitBootstrap()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var first = await Scenario.SignedInBrowserAsync(account);
        using var second = await Scenario.SignedInBrowserAsync(account);
        var oldBinding = await BootstrapAsync(first);
        var newBinding = await BootstrapAsync(second);
        Assert.NotEqual(oldBinding.SessionId, newBinding.SessionId);
        CopySessionCookie(second, first);

        using var stale = await RawAsync(first, HttpMethod.Get, "/api/v1/me/time-zone", oldBinding.Proof);

        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        var reconciled = await BootstrapAsync(first);
        Assert.Equal(newBinding.SessionId, reconciled.SessionId);
        using var valid = await RawAsync(first, HttpMethod.Get, "/api/v1/me/time-zone", reconciled.Proof);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task RefreshingPublicCsrfUnderTheNewCookieDoesNotAuthorizeAnOldPrivateIntent()
    {
        var accountA = await Scenario.CreateConfirmedAccountAsync();
        var accountB = await Scenario.CreateConfirmedAccountAsync();
        using var browserA = await Scenario.SignedInBrowserAsync(accountA);
        using var browserB = await Scenario.SignedInBrowserAsync(accountB);
        CopySessionCookie(browserB, browserA);
        await browserA.FetchCsrfTokenAsync();
        var before = await ReadTimeZoneAsync(accountB.UserId);

        using var response = await RawAsync(browserA, HttpMethod.Put, "/api/v1/me/time-zone",
            browserA.Cookie("XSRF-TOKEN"), new { timeZone = "America/Sao_Paulo" }, $"\"{before.Version}\"");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        Assert.Equal(before, await ReadTimeZoneAsync(accountB.UserId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed-private-proof")]
    public async Task MissingOrMalformedProofCannotReadPrivateState(string? proof)
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        using var response = await RawAsync(browser, HttpMethod.Get, "/api/v1/me/time-zone", proof);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
    }

    [Fact]
    public async Task PrivateProofIsNotAcceptedAsPublicLoginProof()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var binding = await BootstrapAsync(browser);

        using var response = await RawAsync(browser, HttpMethod.Post, "/api/v1/auth/login", binding.Proof,
            new { email = account.Email, password = account.Password });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateHeadValidatesTheProofBeforeCallingItsNextHandler(bool sendProof)
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var binding = await BootstrapAsync(browser);
        var endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Single(candidate => candidate.RoutePattern.RawText?.TrimEnd('/') == "/api/v1/me/time-zone"
                && candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        var context = new DefaultHttpContext { RequestServices = _factory.Services };
        context.Request.Scheme = "https";
        context.Request.Method = "HEAD";
        context.Request.Path = "/api/v1/me/time-zone";
        context.Request.Headers.Cookie = browser.Cookies.GetCookieHeader(Browser.BaseAddress);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, account.UserId.ToString("D")),
                new Claim("duora:sid", binding.SessionId.ToString("D"))], "synthetic-session"));
        context.SetEndpoint(endpoint);
        if (sendProof)
        {
            context.Request.Headers["X-XSRF-TOKEN"] = binding.Proof;
        }
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var entered = false;
        var pipeline = new ApplicationBuilder(_factory.Services);
        pipeline.UseMiddleware<CsrfProtectionMiddleware>();
        pipeline.Run(http =>
        {
            entered = true;
            http.Response.StatusCode = 204;
            return Task.CompletedTask;
        });

        await pipeline.Build()(context);

        Assert.Equal(sendProof, entered);
        Assert.Equal(sendProof ? 204 : 403, context.Response.StatusCode);
    }

    [Fact]
    public async Task RestrictedPasswordBootstrapCarriesContextOnlyOnMeAndDoesNotGrantOtherRoutes()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync(AccountRoles.Operator);
        await Scenario.ScalarAsync(
            "update identity.users set temporary_password_expires_at=now()+interval '1 hour' where id=$1 returning 1",
            account.UserId);
        using var browser = await Scenario.SignedInBrowserAsync(account);

        var binding = await BootstrapAsync(browser, HttpStatusCode.Forbidden);

        Assert.Equal(account.UserId, binding.UserId);
        Assert.Equal("password-change", binding.Phase);
        using var forbidden = await RawAsync(browser, HttpMethod.Get, "/api/v1/me/time-zone", binding.Proof);
        await ProblemAssert.IsProblemAsync(forbidden, HttpStatusCode.Forbidden, "PASSWORD_CHANGE_REQUIRED");
        var body = await forbidden.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.False(body.ContainsKey("sessionContext"));
    }

    [Fact]
    public async Task MfaEnrollmentBootstrapKeepsItsExistingRestrictedAuthorization()
    {
        var account = await Scenario.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);

        var binding = await BootstrapAsync(browser);

        Assert.Equal("mfa-enrollment", binding.Phase);
        using var response = await RawAsync(browser, HttpMethod.Get, "/api/v1/me/time-zone", binding.Proof);
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "MFA_ENROLLMENT_REQUIRED");
    }

    [Fact]
    public async Task AuthenticatedLogoutRequiresItsPrivateProofAndAnonymousLogoutRemainsIdempotent()
    {
        var account = await Scenario.CreateConfirmedAccountAsync();
        using var browser = await Scenario.SignedInBrowserAsync(account);
        var binding = await BootstrapAsync(browser);
        using var publicAttempt = await RawAsync(browser, HttpMethod.Post, "/api/v1/auth/logout", browser.Cookie("XSRF-TOKEN"));
        await ProblemAssert.IsProblemAsync(publicAttempt, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");

        using var privateAttempt = await RawAsync(browser, HttpMethod.Post, "/api/v1/auth/logout", binding.Proof);
        Assert.Equal(HttpStatusCode.NoContent, privateAttempt.StatusCode);
        await browser.FetchCsrfTokenAsync();
        using var anonymousAttempt = await RawAsync(browser, HttpMethod.Post, "/api/v1/auth/logout", browser.Cookie("XSRF-TOKEN"));
        Assert.Equal(HttpStatusCode.NoContent, anonymousAttempt.StatusCode);
    }

    [Fact]
    public void PublicAntiforgeryPurposeHasOnlyTheReviewedBootstrapAndAuthenticationRoutes()
    {
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PublicAntiforgeryEndpoint>() is not null)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText?.TrimEnd('/')}"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(new[]
        {
            "GET /api/v1/auth/csrf",
            "POST /api/v1/auth/confirm-email",
            "POST /api/v1/auth/email-confirmation-requests",
            "POST /api/v1/auth/login",
            "POST /api/v1/auth/password-reset-requests",
            "POST /api/v1/auth/password-resets",
            "POST /api/v1/auth/register",
        }, routes);
    }

    private async Task<Binding> BootstrapAsync(Browser browser, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await RawAsync(browser, HttpMethod.Get, "/api/v1/me", proof: null);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain(response.Headers, header => header.Key.Contains("Session-Id", StringComparison.OrdinalIgnoreCase));
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        var context = body["sessionContext"]!.AsObject();
        var binding = new Binding(context["userId"]!.GetValue<Guid>(), context["sessionId"]!.GetValue<Guid>(),
            context["phase"]!.GetValue<string>(), context["requestToken"]!.GetValue<string>());
        var headers = string.Join("\n", response.Headers.Select(header => string.Join(" ", header.Value)));
        Assert.DoesNotContain(binding.UserId.ToString("D"), headers, StringComparison.Ordinal);
        Assert.DoesNotContain(binding.SessionId.ToString("D"), headers, StringComparison.Ordinal);
        Assert.DoesNotContain(binding.Proof, headers, StringComparison.Ordinal);
        return binding;
    }

    private async Task<HttpResponseMessage> RawAsync(Browser browser, HttpMethod method, string path, string? proof,
        object? body = null, string? ifMatch = null)
    {
        using var client = _factory.CreateDefaultClient(Browser.BaseAddress, new CookieContainerHandler(browser.Cookies));
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", DuoraWebFactory.PublicOrigin);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        if (proof is not null)
        {
            request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", proof);
        }
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static void CopySessionCookie(Browser source, Browser target)
    {
        var value = source.Cookie("__Host-duora");
        Assert.NotNull(value);
        target.Cookies.SetCookies(Browser.BaseAddress, $"__Host-duora={value}; path=/; secure; httponly");
    }

    private async Task<(string? Zone, long Version)> ReadTimeZoneAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "select preferred_time_zone,time_zone_version from identity.users where id=$1", connection);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1));
    }

    private sealed record Binding(Guid UserId, Guid SessionId, string Phase, string Proof);
}
