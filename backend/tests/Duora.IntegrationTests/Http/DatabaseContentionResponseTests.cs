using System.Text.Json.Nodes;
using Duora.Infrastructure.Media;
using Duora.Web.Http;
using Duora.Web.Media;
using Duora.Web.Safety;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.IntegrationTests.Http;

/// <summary>
/// Serialização (40001), deadlock (40P01) e <c>lock_timeout</c> (55P03) são passageiros: em qualquer rota, crus ou
/// embrulhados pelo Npgsql, a resposta é a mesma, 503 <c>TEMPORARILY_UNAVAILABLE</c> com um único
/// <c>Retry-After</c> (#465). As rotas sobem no pipeline de erros da app (<see cref="ApiErrorHandling"/>), com o filtro
/// real de cada família, e o endpoint lança a falha montada.
/// </summary>
public sealed class DatabaseContentionResponseTests(EnabledPhotoUploads media)
    : IClassFixture<EnabledPhotoUploads>
{
    /// <summary>O valor único de <c>Retry-After</c> da contenção, em segundos.</summary>
    private const string ContentionRetryAfterSeconds = "2";

    private readonly string _canary = $"CANARY-{Guid.NewGuid():N}";

    // Cada família é a convenção de erro real que a rota recebe na app; "sem filtro" cai direto no handler global.
    // Participation e Catalog tinham filtro próprio de contenção e deixaram de ter (#465): hoje são como "sem filtro",
    // e as linhas ficam para provar que nenhum filtro volta a responder diferente.
    private static readonly Dictionary<string, Func<RouteHandlerBuilder, RouteHandlerBuilder>> Routes = new()
    {
        ["sem-filtro"] = route => route,
        ["participation"] = route => route,
        ["catalog"] = route => route,
        ["photo-review"] = PhotoReviewEndpoints.Read,
        ["media"] = route => route.RequireMediaAdmission(),
    };

    public static TheoryData<string, string, int> Cases()
    {
        var cases = new TheoryData<string, string, int>();
        foreach (var route in Routes.Keys)
        {
            foreach (var sqlState in new[] { "40001", "40P01", "55P03" })
            {
                for (var wrappings = 0; wrappings <= 2; wrappings++)
                {
                    cases.Add(route, sqlState, wrappings);
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ATransientServerErrorIsTheSameTemporaryUnavailabilityOnEveryRoute(
        string route, string sqlState, int wrappings)
    {
        Exception failure = new PostgresException(_canary, "ERROR", "ERROR", sqlState);
        for (var i = 0; i < wrappings; i++)
        {
            failure = new NpgsqlException(_canary, failure);
        }

        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(route, failure, logs);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/api/v1/teste/{route}", TestContext.Current.CancellationToken);

        var whole = await WholeResponseAsync(response);
        Assert.Equal(ExpectedResponse, whole);
        Assert.DoesNotContain(_canary, whole.Headers, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, whole.RawBody, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, entry =>
            entry.Text.Contains(_canary, StringComparison.Ordinal) || entry.Exception is not null);
        var warning = Assert.Single(logs.Entries, entry =>
            entry.Category == typeof(DatabaseUnavailableExceptionHandler).FullName);
        Assert.Contains($"({sqlState}, {nameof(PostgresException)})", warning.Text, StringComparison.Ordinal);
    }

    private static readonly WholeProblemResponse ExpectedResponse = new(
        503,
        string.Join(
            "\n",
            "Cache-Control: no-store",
            "Content-Type: application/problem+json",
            "Expires: -1",
            "Pragma: no-cache",
            $"Retry-After: {ContentionRetryAfterSeconds}"),
        new JsonObject
        {
            ["code"] = "TEMPORARILY_UNAVAILABLE",
            ["status"] = 503,
            ["title"] = "Service temporarily unavailable. Try again later.",
            ["type"] = "urn:duora:problem:temporarily-unavailable",
        }.ToJsonString(),
        RawBody: "");

    private async Task<WebApplication> StartAsync(string route, Exception failure, CapturingLoggerProvider logs)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(logs);
        builder.Services.AddApiErrorHandling();
        builder.Services.AddSingleton(media.Uploads);
        var app = builder.Build();
        app.UseApiErrorHandling();
        app.UseRouting();
        Routes[route](app.MapGet($"/api/v1/teste/{route}", IResult () => throw failure));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    /// <summary>Status, todos os headers menos <c>Date</c> e o corpo sem <c>traceId</c>, que muda a cada requisição.</summary>
    private static async Task<WholeProblemResponse> WholeResponseAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat<KeyValuePair<string, IEnumerable<string>>>(response.Content.Headers)
            .Where(header => !string.Equals(header.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => $"{header.Key}: {string.Join(",", header.Value)}");
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = JsonNode.Parse(raw)?.AsObject() ?? throw new InvalidOperationException("Sem corpo.");
        problem.Remove("traceId");
        var sorted = new JsonObject();
        foreach (var (key, value) in problem.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            sorted[key] = value?.DeepClone();
        }

        return new WholeProblemResponse((int)response.StatusCode, string.Join("\n", headers), sorted.ToJsonString())
        {
            RawBody = raw,
        };
    }

    /// <summary><see cref="RawBody"/> fica fora da igualdade: serve só para procurar o canário no corpo inteiro.</summary>
    private sealed record WholeProblemResponse(int Status, string Headers, string Body, string RawBody = "")
    {
        public bool Equals(WholeProblemResponse? other) =>
            other is not null && Status == other.Status && Headers == other.Headers && Body == other.Body;

        public override int GetHashCode() => HashCode.Combine(Status, Headers, Body);
    }
}

/// <summary>
/// As famílias de mídia conferem o gate de upload antes de chamar a rota. O <see cref="PhotoUploads"/> vem da app real,
/// com mídia ligada como nos outros testes de integração, uma vez para a classe inteira.
/// </summary>
public sealed class EnabledPhotoUploads(PostgresFixture postgres) : IAsyncLifetime
{
    private DuoraWebFactory? _factory;
    private AsyncServiceScope _scope;

    internal PhotoUploads Uploads { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString);
        _scope = _factory.Services.CreateAsyncScope();
        Uploads = _scope.ServiceProvider.GetRequiredService<PhotoUploads>();
        Assert.True(Uploads.UploadsEnabled);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }
}
