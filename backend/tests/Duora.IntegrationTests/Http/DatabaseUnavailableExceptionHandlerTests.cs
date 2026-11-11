using System.Net.Sockets;
using System.Text.Json.Nodes;
using Duora.Web.Http;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.IntegrationTests.Http;

/// <summary>
/// O Warning do 503 de banco indisponível, sem servidor nem banco: o handler roda sobre um <see cref="DefaultHttpContext"/>
/// com a falha que o middleware de exceção entregaria.
/// </summary>
public sealed class DatabaseUnavailableExceptionHandlerTests
{
    private readonly string _canary = $"CANARY-{Guid.NewGuid():N}";

    [Fact]
    public async Task AServerWithoutFreeConnectionsLogsItsSqlStateWithoutTheDriverMessage()
    {
        var error = new PostgresException(_canary, "FATAL", "FATAL", PostgresErrorCodes.TooManyConnections);

        var warning = await HandleAsync(new InvalidOperationException("transient", error), "/api/v1/auth/login");

        Assert.Contains("53300", warning.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(PostgresException), warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, warning.Text, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public async Task AFailureWithoutSqlStateLogsADash()
    {
        var error = new NpgsqlException("Failed to connect", new SocketException((int)SocketError.ConnectionRefused));

        var warning = await HandleAsync(error, "/api/v1/auth/login");

        Assert.Contains("(-, NpgsqlException)", warning.Text, StringComparison.Ordinal);
    }

    // Sem endpoint, o path é texto do cliente e sem limite de tamanho: não entra no log.
    [Fact]
    public async Task ARequestWithoutEndpointLogsAFixedRouteInsteadOfThePath()
    {
        var error = new PostgresException("indisponível", "FATAL", "FATAL", PostgresErrorCodes.TooManyConnections);

        var warning = await HandleAsync(error, $"/nao-existe/{_canary}");

        Assert.Contains("on (no endpoint)", warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, warning.Text, StringComparison.Ordinal);
    }

    // O embrulhado, uma ou duas vezes, responde como o mesmo erro cru (#465): a resposta inteira é igual, fora o traceId
    // do corpo e o Date. A mensagem dos embrulhos e do erro do servidor não vai para o Warning.
    [Theory]
    [InlineData("40001", 1)]
    [InlineData("40P01", 1)]
    [InlineData("55P03", 1)]
    [InlineData("40001", 2)]
    [InlineData("40P01", 2)]
    [InlineData("55P03", 2)]
    public async Task ARetryableServerErrorWrappedByNpgsqlAnswersLikeTheSameRawError(string sqlState, int wrappings)
    {
        var raw = new PostgresException(_canary, "ERROR", "ERROR", sqlState);
        NpgsqlException wrapped = new PostgresException(_canary, "ERROR", "ERROR", sqlState);
        for (var i = 0; i < wrappings; i++)
        {
            wrapped = new NpgsqlException(_canary, wrapped);
        }

        var (rawResponse, _) = await HandleWithResponseAsync(raw, "/api/v1/auth/login");
        var (wrappedResponse, warning) = await HandleWithResponseAsync(wrapped, "/api/v1/auth/login");

        Assert.Equal(rawResponse, wrappedResponse);
        Assert.Contains($"({sqlState}, {nameof(PostgresException)})", warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, warning.Text, StringComparison.Ordinal);
    }

    private static async Task<CapturedLog> HandleAsync(Exception exception, string path) =>
        (await HandleWithResponseAsync(exception, path)).Warning;

    private static async Task<(WholeProblemResponse Response, CapturedLog Warning)> HandleWithResponseAsync(
        Exception exception, string path)
    {
        using var logs = new CapturingLoggerProvider();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Path = path;
        context.Features.Set<IExceptionHandlerFeature>(
            new ExceptionHandlerFeature { Error = exception, Path = path, Endpoint = null });
        var handler = new DatabaseUnavailableExceptionHandler(loggerFactory.CreateLogger<DatabaseUnavailableExceptionHandler>());

        var handled = await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        var warning = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        return (WholeResponse(context, body), warning);
    }

    /// <summary>Status, todos os headers menos <c>Date</c> e o corpo sem <c>traceId</c>, que muda a cada requisição.</summary>
    private static WholeProblemResponse WholeResponse(HttpContext context, MemoryStream body)
    {
        var headers = context.Response.Headers
            .Where(header => !string.Equals(header.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => $"{header.Key}: {header.Value}");
        var problem = JsonNode.Parse(body.ToArray())?.AsObject() ?? throw new InvalidOperationException("Sem corpo.");
        problem.Remove("traceId");
        return new WholeProblemResponse(context.Response.StatusCode, string.Join("\n", headers), problem.ToJsonString());
    }

    private sealed record WholeProblemResponse(int Status, string Headers, string Body);
}
