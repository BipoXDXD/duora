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

    // O embrulhado imita o 503 que a main já dava a ele, o mesmo da indisponibilidade crua do servidor.
    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("55P03")]
    public async Task ARetryableServerErrorWrappedByNpgsqlAnswersLikeARawUnavailableServer(string sqlState)
    {
        var raw = new PostgresException("indisponível", "FATAL", "FATAL", PostgresErrorCodes.TooManyConnections);
        var wrapped = new NpgsqlException("driver", new PostgresException("erro comum", "ERROR", "ERROR", sqlState));

        var (rawResponse, _) = await HandleWithResponseAsync(raw, "/api/v1/auth/login");
        var (wrappedResponse, warning) = await HandleWithResponseAsync(wrapped, "/api/v1/auth/login");

        Assert.Equal(rawResponse, wrappedResponse);
        Assert.Contains($"({sqlState}, {nameof(PostgresException)})", warning.Text, StringComparison.Ordinal);
    }

    private static async Task<CapturedLog> HandleAsync(Exception exception, string path) =>
        (await HandleWithResponseAsync(exception, path)).Warning;

    private static async Task<(ProblemResponse Response, CapturedLog Warning)> HandleWithResponseAsync(
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
        var problem = JsonNode.Parse(body.ToArray());
        var response = new ProblemResponse(
            context.Response.StatusCode, (string?)problem?["code"], context.Response.Headers.RetryAfter.ToString());
        return (response, warning);
    }

    private sealed record ProblemResponse(int Status, string? Code, string RetryAfter);
}
