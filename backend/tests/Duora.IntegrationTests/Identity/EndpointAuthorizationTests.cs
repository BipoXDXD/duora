using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Negar por padrão: toda rota registrada exige sessão, salvo a allowlist pública. Uma rota nova sem decisão
/// explícita quebra este teste, em vez de nascer aberta.
/// </summary>
public sealed class EndpointAuthorizationTests(PostgresFixture postgres) : IAsyncDisposable
{
    private static readonly string[] PublicRoutes =
    [
        "GET /health/live",
        "GET /health/ready",
        "GET /api/v1/auth/csrf",
        "GET /api/v1/legal-documents",
        "POST /api/v1/auth/register",
        "POST /api/v1/auth/confirm-email",
        "POST /api/v1/auth/email-confirmation-requests",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/logout",
        "POST /api/v1/auth/password-reset-requests",
        "POST /api/v1/auth/password-resets",
        // Webhooks de fornecedor: sem sessão e sem CSRF, autenticados pela assinatura (ADR-0024).
        "POST /webhooks/{provider}",
    ];

    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void OnlyTheAllowlistIsAnonymous()
    {
        var anonymous = Routes()
            .Where(route => route.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(route => route.Name);

        Assert.Equal(PublicRoutes.Order(StringComparer.Ordinal), anonymous.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryOtherRouteAnswers401WithoutSession()
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();
        var protectedRoutes = Routes().Where(route => !PublicRoutes.Contains(route.Name)).ToList();

        Assert.NotEmpty(protectedRoutes);
        foreach (var route in protectedRoutes)
        {
            using var response = await SendAsync(browser, route);
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{route.Name} respondeu {(int)response.StatusCode} sem sessão.");
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        }
    }

    /// <summary>
    /// Toda rota com sessão pode responder 403 de sessão restrita (<c>PASSWORD_CHANGE_REQUIRED</c> ou
    /// <c>MFA_ENROLLMENT_REQUIRED</c>, <see cref="Duora.Web.Security.SessionAuthorization"/>), inclusive <c>GET /me</c>, que a
    /// SPA lê para decidir a fase restrita. O contrato gerado precisa dizer isso.
    /// </summary>
    [Fact]
    public void EveryProtectedRouteDocumentsTheRestrictedSession403()
    {
        var undocumented = Routes()
            .Where(route => !PublicRoutes.Contains(route.Name))
            .Where(route => route.Endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>() is not
            {
                ExcludeFromDescription: true,
            })
            .Where(route => !route.Endpoint.Metadata
                .OfType<IProducesResponseTypeMetadata>()
                .Any(response => response.StatusCode == StatusCodes.Status403Forbidden))
            .Select(route => route.Name)
            .ToList();

        Assert.True(undocumented.Count == 0, $"Sem 403 no contrato: {string.Join(", ", undocumented)}");
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

    private static Task<HttpResponseMessage> SendAsync(Browser browser, (string Name, string Method, string Path, RouteEndpoint Endpoint) route)
    {
        // Parâmetros de rota recebem um GUID qualquer: a resposta tem de ser 401 antes de olhar o recurso.
        var path = System.Text.RegularExpressions.Regex.Replace(route.Path, "{[^}]+}", Guid.NewGuid().ToString());
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
