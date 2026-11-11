using System.Reflection;
using Duora.Web.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// A regra dos 30 s vale para o grupo <c>/auth</c> inteiro (decisão da coordenação, 10/11): o marcador
/// <see cref="NeutralUnavailabilityEndpoint"/> fica no grupo, e toda rota nova ali herda a regra. Estes testes leem as
/// rotas registradas na app real, como <see cref="EndpointAuthorizationTests"/>, e quebram se uma rota de
/// <c>/auth</c> perder o marcador ou se uma rota pública de identidade nascer fora de <c>/auth</c>.
/// </summary>
/// <remarks>
/// "Rota pública de identidade", de forma verificável: rota anônima (<see cref="IAllowAnonymous"/>) cujo handler recebe
/// e-mail, senha, token, código ou credencial, seja num parâmetro com esse nome, seja numa propriedade do DTO de
/// <c>Duora.Contracts</c> que o handler recebe.
/// </remarks>
public sealed class NeutralUnavailabilityRoutesTests(PostgresFixture postgres) : IAsyncDisposable
{
    private const string AuthPrefix = "/api/v1/auth/";

    private static readonly string[] SensitiveInputs = ["email", "password", "token", "code", "credential"];

    /// <summary>As três rotas que a decisão nomeia; a lista explícita não depende do critério acima.</summary>
    private static readonly string[] NamedRoutes =
    [
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/register",
        "POST /api/v1/auth/email-confirmation-requests",
    ];

    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void LoginRegistrationAndResendCarryTheMarker()
    {
        var marked = Routes().Where(route => IsMarked(route.Endpoint)).Select(route => route.Name).ToHashSet();

        Assert.All(NamedRoutes, name => Assert.Contains(name, marked));
    }

    [Fact]
    public void EveryRouteUnderAuthCarriesTheMarker()
    {
        var unmarked = Routes()
            .Where(route => route.Path.StartsWith(AuthPrefix, StringComparison.Ordinal) && !IsMarked(route.Endpoint))
            .Select(route => route.Name)
            .ToList();

        Assert.True(unmarked.Count == 0, $"Rotas de /auth sem o marcador: {string.Join(", ", unmarked)}");
    }

    [Fact]
    public void EveryPublicIdentityRouteLivesUnderAuth()
    {
        var identity = Routes().Where(route => IsPublicIdentityRoute(route.Endpoint)).ToList();

        // O critério precisa reconhecer ao menos as rotas nomeadas, ou não prova nada.
        Assert.All(NamedRoutes, name => Assert.Contains(identity, route => route.Name == name));
        var outside = identity
            .Where(route => !route.Path.StartsWith(AuthPrefix, StringComparison.Ordinal) || !IsMarked(route.Endpoint))
            .Select(route => route.Name)
            .ToList();
        Assert.True(outside.Count == 0, $"Rotas públicas de identidade fora de /auth: {string.Join(", ", outside)}");
    }

    private static bool IsMarked(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<NeutralUnavailabilityEndpoint>() is not null;

    private static bool IsPublicIdentityRoute(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
            || endpoint.Metadata.OfType<MethodInfo>().FirstOrDefault() is not { } handler)
        {
            return false;
        }

        return handler.GetParameters().Any(parameter => IsSensitive(parameter.Name)
            || (parameter.ParameterType.Namespace?.StartsWith("Duora.Contracts", StringComparison.Ordinal) == true
                && parameter.ParameterType.GetProperties().Any(property => IsSensitive(property.Name))));
    }

    private static bool IsSensitive(string? name) =>
        name is not null && SensitiveInputs.Any(input => name.Contains(input, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<(string Name, string Path, RouteEndpoint Endpoint)> Routes()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var path = "/" + (endpoint.RoutePattern.RawText ?? "").Trim('/');
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                yield return ($"{method} {path}", path, endpoint);
            }
        }
    }
}
