using System.Net.Sockets;
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

    private static async Task<CapturedLog> HandleAsync(Exception exception, string path)
    {
        using var logs = new CapturingLoggerProvider();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Features.Set<IExceptionHandlerFeature>(
            new ExceptionHandlerFeature { Error = exception, Path = path, Endpoint = null });
        var handler = new DatabaseUnavailableExceptionHandler(loggerFactory.CreateLogger<DatabaseUnavailableExceptionHandler>());

        var handled = await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        var warning = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        return warning;
    }
}
