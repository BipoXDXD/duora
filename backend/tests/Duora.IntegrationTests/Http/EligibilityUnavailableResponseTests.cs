using System.Net.Sockets;
using System.Text.Json.Nodes;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.IntegrationTests.Http;

/// <summary>
/// O 503 da elegibilidade indisponível (<see cref="EligibilityUnavailableReason"/>) é o mesmo do handler global de
/// banco fora (<see cref="DatabaseUnavailableExceptionHandler"/>) para a mesma falha: status, corpo sem
/// <c>traceId</c> e headers iguais, sem <c>Retry-After</c> próprio (#473). Sem servidor nem banco: os dois rodam sobre
/// um <see cref="DefaultHttpContext"/>.
/// </summary>
public sealed class EligibilityUnavailableResponseTests
{
    private const string UnreachableRetryAfter = "5";
    private const string ContentionRetryAfter = "2";

    // Fora das rotas de identidade, a contenção responde 2 e o resto 5; o literal esperado vem da decisão (A) da
    // coordenação de 10/11 (#465), não da fonte, para a regra não poder mudar em silêncio.
    public static TheoryData<string, string> Causes => new()
    {
        { "unreachable", UnreachableRetryAfter },
        { "too-many-connections", UnreachableRetryAfter },
        { "serialization", ContentionRetryAfter },
        { "deadlock", ContentionRetryAfter },
        { "lock-timeout", ContentionRetryAfter },
        { "serialization-wrapped-by-ef", ContentionRetryAfter },
        { "serialization-wrapped-by-npgsql", ContentionRetryAfter },
    };

    [Theory]
    [MemberData(nameof(Causes))]
    public async Task EligibilityUnavailableAnswersLikeTheGlobalHandlerForTheSameFailure(string cause, string retryAfter)
    {
        var failure = Failure(cause);

        var global = await GlobalHandlerAsync(failure, neutral: false);
        var eligibility = await EligibilityAsync(failure, neutral: false);

        Assert.Equal(global, eligibility);
        Assert.Contains($"Retry-After: {retryAfter}", eligibility.Headers, StringComparison.Ordinal);
    }

    // Nas rotas de identidade o Retry-After é um valor único, qualquer que seja a causa (#468, #469).
    [Theory]
    [InlineData("unreachable")]
    [InlineData("serialization")]
    public async Task OnIdentityRoutesEligibilityUnavailableUsesTheSingleBusyRetryAfter(string cause)
    {
        var failure = Failure(cause);
        var busy = (int)new AccountLockOptions().BusyRetryAfter.TotalSeconds;

        var global = await GlobalHandlerAsync(failure, neutral: true);
        var eligibility = await EligibilityAsync(failure, neutral: true);

        Assert.Equal(global, eligibility);
        Assert.Contains($"Retry-After: {busy}", eligibility.Headers, StringComparison.Ordinal);
    }

    // A elegibilidade já tratava qualquer DbException como indisponibilidade: o que o handler global deixaria virar
    // 500 (erro de SQL comum) continua 503 aqui, com o Retry-After de banco fora.
    [Fact]
    public async Task ADatabaseErrorThatIsNotTemporaryStillAnswersUnavailableWithTheUnreachableRetryAfter()
    {
        var failure = new PostgresException("erro", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);

        var eligibility = await EligibilityAsync(failure, neutral: false);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, eligibility.Status);
        Assert.Contains($"Retry-After: {UnreachableRetryAfter}", eligibility.Headers, StringComparison.Ordinal);
    }

    private static Exception Failure(string cause) => cause switch
    {
        "unreachable" => new NpgsqlException("Failed to connect", new SocketException((int)SocketError.ConnectionRefused)),
        "too-many-connections" => Server(PostgresErrorCodes.TooManyConnections),
        "serialization" => Server(PostgresErrorCodes.SerializationFailure),
        "deadlock" => Server(PostgresErrorCodes.DeadlockDetected),
        "lock-timeout" => Server(PostgresErrorCodes.LockNotAvailable),
        "serialization-wrapped-by-ef" =>
            new InvalidOperationException("transient failure", Server(PostgresErrorCodes.SerializationFailure)),
        "serialization-wrapped-by-npgsql" =>
            new NpgsqlException("wrapped", Server(PostgresErrorCodes.SerializationFailure)),
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, null),
    };

    private static PostgresException Server(string sqlState) => new("erro", "ERROR", "ERROR", sqlState);

    private static async Task<WholeProblemResponse> GlobalHandlerAsync(Exception failure, bool neutral)
    {
        using var services = Services();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var (context, body) = NewContext(services);
        context.Features.Set<IExceptionHandlerFeature>(
            new ExceptionHandlerFeature { Error = failure, Path = "/api/v1/x", Endpoint = EndpointFor(neutral) });
        var handler = new DatabaseUnavailableExceptionHandler(
            loggerFactory.CreateLogger<DatabaseUnavailableExceptionHandler>());

        Assert.True(await handler.TryHandleAsync(context, failure, TestContext.Current.CancellationToken));
        return WholeResponse(context, body);
    }

    private static async Task<WholeProblemResponse> EligibilityAsync(Exception failure, bool neutral)
    {
        using var services = Services();
        var (context, body) = NewContext(services);
        context.SetEndpoint(EndpointFor(neutral));
        var reason = new EligibilityUnavailableReason(new StubHandler(), failure);
        var forbidden = PolicyAuthorizationResult.Forbid(AuthorizationFailure.Failed([reason]));
        var results = new SessionAuthorizationResultHandler(services.GetRequiredService<IAntiforgery>());

        await results.HandleAsync(_ => Task.CompletedTask, context, new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build(), forbidden);
        return WholeResponse(context, body);
    }

    private static ServiceProvider Services() => new ServiceCollection()
        .AddLogging()
        .AddAntiforgery()
        .AddSingleton(Options.Create(new AccountLockOptions()))
        .BuildServiceProvider();

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext(IServiceProvider services)
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;
        context.Request.Path = "/api/v1/x";
        return (context, body);
    }

    private static Endpoint EndpointFor(bool neutral) => new(
        _ => Task.CompletedTask,
        new EndpointMetadataCollection(neutral ? [new NeutralUnavailabilityEndpoint()] : []),
        "test");

    /// <summary>Status, todos os headers menos <c>Date</c> e o corpo sem <c>traceId</c>, que muda a cada requisição.</summary>
    private static WholeProblemResponse WholeResponse(HttpContext context, MemoryStream body)
    {
        var headers = context.Response.Headers
            .Where(header => !string.Equals(header.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => $"{header.Key}: {header.Value}");
        var problem = JsonNode.Parse(body.ToArray())?.AsObject() ?? throw new InvalidOperationException("Sem corpo.");
        problem.Remove("traceId");
        return new WholeProblemResponse(
            context.Response.StatusCode, string.Join("\n", headers), problem.ToJsonString());
    }

    private sealed record WholeProblemResponse(int Status, string Headers, string Body);

    private sealed class StubHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context) => Task.CompletedTask;
    }
}
