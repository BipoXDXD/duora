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
/// Serialização (40001), deadlock (40P01) e <c>lock_timeout</c> (55P03) são passageiros: em qualquer rota fora de
/// identidade, crus ou embrulhados (Npgsql ou EF), a resposta é a mesma, 503 <c>TEMPORARILY_UNAVAILABLE</c> com o
/// <c>Retry-After</c> da contenção; banco fora do alcance responde o mesmo 503 com o <c>Retry-After</c> próprio (#465).
/// As rotas sobem no pipeline de erros da app (<see cref="ApiErrorHandling"/>), com o filtro real de cada família que
/// ainda tem um, e o endpoint lança a falha montada. As rotas de identidade têm valor próprio
/// (<c>NeutralUnavailabilityResponseTests</c>).
/// </summary>
public sealed class DatabaseContentionResponseTests(EnabledPhotoUploads media)
    : IClassFixture<EnabledPhotoUploads>
{
    /// <summary>Decisão da coordenação, 10/11: contenção 2 s, banco fora do alcance 5 s.</summary>
    private const string ContentionRetryAfterSeconds = "2";

    private const string UnreachableRetryAfterSeconds = "5";

    private readonly string _canary = $"CANARY-{Guid.NewGuid():N}";

    // Cada família é a convenção de erro real que a rota recebe na app; "sem filtro" cai direto no handler global.
    // Participation e Catalog não têm mais filtro de contenção: ContentionClassificationTests impede que volte um.
    private static readonly Dictionary<string, Func<RouteHandlerBuilder, RouteHandlerBuilder>> Routes = new()
    {
        ["sem-filtro"] = route => route,
        ["photo-review"] = PhotoReviewEndpoints.Read,
        ["media"] = route => route.RequireMediaAdmission(),
    };

    /// <summary>Como a falha do servidor chega ao pipeline: crua, embrulhada pelo Npgsql (1 ou 2 vezes) ou pelo EF.</summary>
    private static readonly string[] Envelopes = ["cru", "npgsql", "npgsql-npgsql", "ef"];

    public static TheoryData<string, string, string> ContentionCases()
    {
        var cases = new TheoryData<string, string, string>();
        foreach (var route in Routes.Keys)
        {
            foreach (var sqlState in new[] { "40001", "40P01", "55P03" })
            {
                foreach (var envelope in Envelopes)
                {
                    cases.Add(route, sqlState, envelope);
                }
            }
        }

        return cases;
    }

    public static TheoryData<string, string> UnreachableCases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var route in Routes.Keys)
        {
            foreach (var cause in new[] { "53300", "57P01", "08006", "cliente-timeout" })
            {
                cases.Add(route, cause);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(ContentionCases))]
    public async Task ATransientServerErrorIsTheSameTemporaryUnavailabilityOnEveryRoute(
        string route, string sqlState, string envelope)
    {
        var failure = Envelope(new PostgresException(_canary, "ERROR", "ERROR", sqlState), envelope);

        var (whole, logs) = await RespondAsync(route, failure);

        Assert.Equal(Expected(ContentionRetryAfterSeconds), whole);
        AssertNothingLeaks(whole, logs);
        var warning = SingleHandlerWarning(logs);
        Assert.Equal("DatabaseContention", warning.EventId.Name);
        Assert.Equal(2, warning.EventId.Id);
        Assert.Contains($"({sqlState}, {nameof(PostgresException)})", warning.Text, StringComparison.Ordinal);
    }

    /// <summary>Banco fora do alcance tem <c>Retry-After</c> próprio: trocar o valor para o da contenção quebra aqui.</summary>
    [Theory]
    [MemberData(nameof(UnreachableCases))]
    public async Task AnUnreachableDatabaseIsTheSameLongerUnavailabilityOnEveryRoute(string route, string cause)
    {
        Exception failure = cause == "cliente-timeout"
            ? new NpgsqlException(_canary, new TimeoutException(_canary))
            : new PostgresException(_canary, "FATAL", "FATAL", cause);

        var (whole, logs) = await RespondAsync(route, failure);

        Assert.Equal(Expected(UnreachableRetryAfterSeconds), whole);
        AssertNothingLeaks(whole, logs);
        var warning = SingleHandlerWarning(logs);
        Assert.Equal("DatabaseUnavailable", warning.EventId.Name);
        Assert.Equal(1, warning.EventId.Id);
        var described = cause == "cliente-timeout" ? $"(-, {nameof(NpgsqlException)})" : $"({cause}, {nameof(PostgresException)})";
        Assert.Contains(described, warning.Text, StringComparison.Ordinal);
    }

    private Exception Envelope(PostgresException server, string envelope) => envelope switch
    {
        "cru" => server,
        "npgsql" => new NpgsqlException(_canary, server),
        "npgsql-npgsql" => new NpgsqlException(_canary, new NpgsqlException(_canary, server)),
        // A estratégia de execução do EF desiste com "transient failure" e a falha do Npgsql dentro.
        "ef" => new InvalidOperationException(_canary, new NpgsqlException(_canary, server)),
        _ => throw new ArgumentOutOfRangeException(nameof(envelope), envelope, null),
    };

    private async Task<(WholeProblemResponse Whole, CapturingLoggerProvider Logs)> RespondAsync(string route, Exception failure)
    {
        var logs = new CapturingLoggerProvider();
        await using var app = await StartAsync(route, failure, logs);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/v1/teste/{route}", TestContext.Current.CancellationToken);
        return (await WholeResponseAsync(response), logs);
    }

    private void AssertNothingLeaks(WholeProblemResponse whole, CapturingLoggerProvider logs)
    {
        Assert.DoesNotContain(_canary, whole.Headers, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, whole.RawBody, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, entry =>
            entry.Text.Contains(_canary, StringComparison.Ordinal) || entry.Exception is not null);
    }

    private static CapturedLog SingleHandlerWarning(CapturingLoggerProvider logs) =>
        Assert.Single(logs.Entries, entry => entry.Category == typeof(DatabaseUnavailableExceptionHandler).FullName);

    private static WholeProblemResponse Expected(string retryAfterSeconds) => new(
        503,
        string.Join(
            "\n",
            "Cache-Control: no-store",
            "Content-Type: application/problem+json",
            "Expires: -1",
            "Pragma: no-cache",
            $"Retry-After: {retryAfterSeconds}"),
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
    private PhotoUploads? _uploads;

    internal PhotoUploads Uploads => _uploads ?? throw new InvalidOperationException("Fixture não inicializada.");

    public ValueTask InitializeAsync()
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString);
        _scope = _factory.Services.CreateAsyncScope();
        _uploads = _scope.ServiceProvider.GetRequiredService<PhotoUploads>();
        Assert.True(_uploads.UploadsEnabled);
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
