using System.Net;
using System.Text.RegularExpressions;
using Duora.IntegrationTests.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Negar por padrão para a conta suspensa (spec 8.2, issue #299): toda rota com sessão responde 403
/// <c>ACCOUNT_SUSPENDED</c>, exceto a allowlist do estado da própria conta e da segurança dela. Uma rota nova sem
/// decisão explícita quebra este teste, em vez de nascer aberta à conta suspensa.
/// </summary>
public sealed class SuspendedAccountRoutesTests : IAsyncDisposable
{
    /// <summary>
    /// Allowlist da conta suspensa: estado da própria conta (A08), sessões (A09, A10), MFA (A11, A12, A14, A15),
    /// reautenticação (A13), troca de senha (A16) e, por privacidade, achar e excluir as próprias fotos (M02, M03 e a
    /// lista do titular). Login, logout e demais rotas públicas não exigem sessão. O recurso de moderação (API-S06)
    /// entra aqui quando existir.
    /// </summary>
    private static readonly string[] SuspendedAllowlist =
    [
        "GET /api/v1/me",
        "GET /api/v1/me/sessions",
        "DELETE /api/v1/me/sessions/{sessionId:guid}",
        "POST /api/v1/me/mfa/enrollments",
        "POST /api/v1/me/mfa/confirmations",
        "POST /api/v1/me/mfa/recovery-codes",
        "DELETE /api/v1/me/mfa",
        "POST /api/v1/auth/reauthenticate",
        "POST /api/v1/me/password-changes",
        "GET /api/v1/me/photos",
        "GET /api/v1/me/photos/{photoId:guid}/status",
        "DELETE /api/v1/me/photos/{photoId:guid}",
    ];

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly AccountSuspensionScenario _scenario;

    public SuspendedAccountRoutesTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
        _scenario = new AccountSuspensionScenario(postgres.ConnectionString, _factory, _time);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void TheAllowlistIsExactlyTheRoutesMarkedForTheSuspendedAccount()
    {
        var marked = Routes()
            .Where(route => route.Endpoint.Metadata.GetMetadata<SuspendedAccountAllowedEndpoint>() is not null)
            .Select(route => route.Name);

        Assert.Equal(SuspendedAllowlist.Order(StringComparer.Ordinal), marked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryRouteWithSessionOutsideTheAllowlistIsForbiddenToTheSuspendedAccount()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        using var browser = await _scenario.Identity.SignedInBrowserAsync(participant);
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using (moderator)
        {
            await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);
        }

        var withSession = Routes()
            .Where(route => route.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .ToList();
        var allowed = new List<string>();
        foreach (var route in withSession.Where(route => !SuspendedAllowlist.Contains(route.Name)))
        {
            using var response = await SendAsync(browser, route);
            var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "ACCOUNT_SUSPENDED");
            Assert.DoesNotContain(participant.Email, body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        }

        foreach (var route in withSession.Where(route => SuspendedAllowlist.Contains(route.Name)))
        {
            using var response = await SendAsync(browser, route);
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                allowed.Add(route.Name);
            }
        }

        // A enumeração precisa enxergar as rotas de participação e de administração, não só as da conta.
        Assert.Contains(withSession, route => route.Name == "GET " + AccountSuspensionScenario.ParticipationSentinel);
        Assert.Contains(withSession, route => route.Name.StartsWith("POST /api/v1/admin/", StringComparison.Ordinal));
        Assert.Equal(SuspendedAllowlist.Order(StringComparer.Ordinal), allowed.Order(StringComparer.Ordinal));
    }

    private IEnumerable<(string Name, string Method, string Path, RouteEndpoint Endpoint)> Routes()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var path = "/" + (endpoint.RoutePattern.RawText ?? "").Trim('/');
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                yield return ($"{method} {path}", method, path, endpoint);
            }
        }
    }

    private static Task<HttpResponseMessage> SendAsync(
        Browser browser,
        (string Name, string Method, string Path, RouteEndpoint Endpoint) route)
    {
        // Parâmetros de rota recebem um GUID qualquer (ou um valor do formato da restrição).
        var path = Regex.Replace(route.Path, "{[^}]+}", Guid.NewGuid().ToString());
        return route.Method switch
        {
            "GET" => browser.GetAsync(path),
            "DELETE" => browser.DeleteAsync(path),
            "PUT" => browser.PutAsync(path),
            "POST" => browser.PostJsonAsync(path, new { }),
            "PATCH" => browser.PatchRawJsonAsync(path, "{}", ifMatch: "\"0\""),
            var method => throw new InvalidOperationException($"Método {method} sem caso no teste."),
        };
    }
}
