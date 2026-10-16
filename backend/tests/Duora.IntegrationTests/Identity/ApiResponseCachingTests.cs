using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// T-21 e T-14, lado do servidor: nenhuma resposta da API fica guardada em cache de navegador ou proxy (spec 8.1).
/// Sem isso, depois do logout o voltar do navegador ou um proxy poderia reapresentar a resposta privada de quem saiu
/// para quem entra em seguida. Enumera as rotas registradas: rota nova já nasce coberta.
/// </summary>
[Trait("Acceptance", "T-21")]
public sealed class ApiResponseCachingTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task PrivateReadsWithSessionAreNotStored()
    {
        var account = await new IdentityScenario(postgres, _factory).CreateConfirmedAccountAsync();
        using var browser = await new IdentityScenario(postgres, _factory).SignedInBrowserAsync(account);
        var reads = ApiRoutes().Where(route => route.Method == "GET").ToList();

        Assert.NotEmpty(reads);
        foreach (var route in reads)
        {
            using var response = await browser.GetAsync(WithRouteValues(route.Path));
            AssertNotStored(route.Name, response);
        }
    }

    [Fact]
    public async Task EveryRouteWithoutSessionIsNotStored()
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        foreach (var route in ApiRoutes())
        {
            var path = WithRouteValues(route.Path);
            using var response = route.Method switch
            {
                "GET" => await browser.GetAsync(path),
                "POST" => await browser.PostJsonAsync(path, new { }),
                "DELETE" => await browser.DeleteAsync(path),
                var method => throw new InvalidOperationException($"Método {method} sem caso no teste."),
            };
            AssertNotStored(route.Name, response);
        }
    }

    [Fact]
    public async Task UnmatchedApiPathIsNotStored()
    {
        using var browser = new Browser(_factory);

        // Sem rota, a política de fallback responde antes (401); o cabeçalho vale para qualquer resposta sob /api.
        using var response = await browser.GetAsync("/api/v1/rota-que-nao-existe");

        AssertNotStored("GET /api/v1/rota-que-nao-existe", response);
    }

    private static void AssertNotStored(string route, HttpResponseMessage response)
    {
        Assert.True(
            response.Headers.CacheControl?.NoStore == true,
            $"{route} respondeu {(int)response.StatusCode} com Cache-Control '{response.Headers.CacheControl}'.");
        Assert.Contains("no-cache", response.Headers.Pragma.Select(pragma => pragma.Name));
    }

    /// <summary>Parâmetros de rota recebem um GUID qualquer: o que importa é o cabeçalho, qualquer que seja o status.</summary>
    private static string WithRouteValues(string pattern) =>
        Regex.Replace(pattern, "{[^}]+}", Guid.NewGuid().ToString());

    private List<(string Name, string Method, string Path)> ApiRoutes()
    {
        var routes = new List<(string Name, string Method, string Path)>();
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var path = "/" + (endpoint.RoutePattern.RawText ?? "").Trim('/');
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                routes.Add(($"{method} {path}", method, path));
            }
        }

        return routes;
    }
}
