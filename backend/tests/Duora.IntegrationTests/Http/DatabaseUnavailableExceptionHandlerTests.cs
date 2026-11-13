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
/// O Warning do 503 de banco indisponível, sem servidor nem banco: o handler roda sobre um
/// <see cref="DefaultHttpContext"/> com a falha que o middleware de exceção entregaria.
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

    // O embrulhado, uma ou duas vezes, responde como o mesmo erro cru (#465): a resposta inteira é igual, fora o
    // traceId do corpo e o Date. A mensagem dos embrulhos e do erro do servidor não vai para o Warning.
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

    // O IsTransient do Npgsql cobre 55000 (costuma ser defeito permanente) e 58000 (genérico demais), mas a lista
    // própria os deixa de fora de propósito (#492): crus, não são indisponibilidade e seguem para o 500.
    [Theory]
    [InlineData(PostgresErrorCodes.ObjectNotInPrerequisiteState)]
    [InlineData(PostgresErrorCodes.SystemError)]
    public async Task APermanentLookingServerErrorIsNotUnavailabilityWhenRaw(string sqlState)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var raw = new PostgresException(_canary, "ERROR", "ERROR", sqlState);
        var handler = new DatabaseUnavailableExceptionHandler(
            services.GetRequiredService<ILogger<DatabaseUnavailableExceptionHandler>>());

        var handled = await handler.TryHandleAsync(context, raw, TestContext.Current.CancellationToken);

        Assert.False(handled);
    }

    // O IsTransient do Npgsql 10.0.3 cobre da classe 08 só 08000, 08001, 08003, 08004, 08006 e 08007 (#497). 08P01
    // (protocol_violation) é defeito de código ou de proxy, como um bind com número de parâmetros errado, e 08002
    // (connection_already_exists) também não é a rede fora: crus, não são indisponibilidade, seguem para o 500 do
    // middleware de exceção, sem Warning de indisponibilidade, inclusive embrulhados pelo Npgsql.
    [Theory]
    [InlineData("08P01")]
    [InlineData("08002")]
    public async Task AConnectionClassStateOutsideTheListIsNotUnavailabilityWhenRaw(string sqlState)
    {
        using var logs = new CapturingLoggerProvider();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var context = new DefaultHttpContext { RequestServices = services };
        var raw = new PostgresException(_canary, "ERROR", "ERROR", sqlState);
        var handler = new DatabaseUnavailableExceptionHandler(
            loggerFactory.CreateLogger<DatabaseUnavailableExceptionHandler>());

        var handled = await handler.TryHandleAsync(context, raw, TestContext.Current.CancellationToken);

        Assert.False(handled);
        Assert.Empty(logs.Entries);
        Assert.False(DatabaseUnavailability.IsTemporary(raw));
        Assert.False(DatabaseUnavailability.IsTemporary(new InvalidOperationException("wrapped", raw)));
        Assert.False(DatabaseUnavailability.IsTemporary(new NpgsqlException("driver", raw)));
        Assert.False(DatabaseUnavailability.IsTemporary(
            new NpgsqlException("driver", new NpgsqlException("driver", raw))));
    }

    // Cada estado da lista explícita da classe 08 segue 503 com Retry-After de banco fora do alcance (5 s), crus.
    [Theory]
    [InlineData("08000")]
    [InlineData("08001")]
    [InlineData("08003")]
    [InlineData("08004")]
    [InlineData("08006")]
    [InlineData("08007")]
    public async Task EachConnectionClassStateOnTheListIsUnavailabilityWhenRaw(string sqlState)
    {
        var raw = new PostgresException(_canary, "ERROR", "ERROR", sqlState);

        var (response, warning) = await HandleWithResponseAsync(raw, "/api/v1/auth/login");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.Status);
        Assert.Contains("Retry-After: 5", response.Headers, StringComparison.Ordinal);
        Assert.Contains($"({sqlState}, {nameof(PostgresException)})", warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, warning.Text, StringComparison.Ordinal);
    }

    // Já embrulhados, 55000 e 58000 seguem 503: quem decide é o IsTransient do Npgsql sobre a NpgsqlException, não a
    // lista própria (#492). Fixa o comportamento atual; o cru é o teste acima.
    [Theory]
    [InlineData(PostgresErrorCodes.ObjectNotInPrerequisiteState, 1)]
    [InlineData(PostgresErrorCodes.SystemError, 1)]
    [InlineData(PostgresErrorCodes.ObjectNotInPrerequisiteState, 2)]
    [InlineData(PostgresErrorCodes.SystemError, 2)]
    public async Task APermanentLookingServerErrorWrappedByNpgsqlIsStillUnavailability(string sqlState, int wrappings)
    {
        NpgsqlException wrapped = new PostgresException(_canary, "ERROR", "ERROR", sqlState);
        for (var i = 0; i < wrappings; i++)
        {
            wrapped = new NpgsqlException(_canary, wrapped);
        }

        var (_, warning) = await HandleWithResponseAsync(wrapped, "/api/v1/auth/login");

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
        var handler = new DatabaseUnavailableExceptionHandler(
            loggerFactory.CreateLogger<DatabaseUnavailableExceptionHandler>());

        var handled = await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        var warning = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        return (WholeResponse(context, body), warning);
    }

    /// <summary>
    /// Status, todos os headers menos <c>Date</c> e o corpo sem <c>traceId</c>, que muda a cada requisição.
    /// </summary>
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
}
