using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace Duora.Web.Http;

/// <summary>
/// Banco temporariamente fora do alcance: conexão recusada ou caída, pool esgotado (o <c>Timeout</c> do pool venceu)
/// ou servidor sem conexões livres (53300) ou desligando (57P01 a 57P03, classe 08). O EF costuma embrulhar a causa
/// numa <see cref="InvalidOperationException"/> ("transient failure"), então a cadeia inteira é examinada.
/// </summary>
internal static class DatabaseUnavailability
{
    public static bool IsTemporary(Exception exception) => TemporaryCause(exception) is not null;

    /// <summary>A exceção da cadeia que caracteriza a indisponibilidade, ou nula se a falha for outra.</summary>
    public static NpgsqlException? TemporaryCause(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case PostgresException server when IsUnavailableState(server.SqlState):
                    return server;
                case PostgresException:
                    // Erro de SQL comum (constraint, sintaxe, serialização): não é indisponibilidade.
                    return null;
                case NpgsqlException client when client.IsTransient:
                    // Falha de rede, conexão recusada ou pool esgotado: o servidor nem respondeu.
                    return client;
            }
        }

        return null;
    }

    private static bool IsUnavailableState(string sqlState) =>
        sqlState is PostgresErrorCodes.TooManyConnections
            or PostgresErrorCodes.AdminShutdown
            or PostgresErrorCodes.CrashShutdown
            or PostgresErrorCodes.CannotConnectNow
        || sqlState.StartsWith("08", StringComparison.Ordinal);
}

/// <summary>
/// Banco indisponível vira 503 <c>TEMPORARILY_UNAVAILABLE</c> com <c>Retry-After</c>, sem detalhe (spec 11; auth.md,
/// "Banco indisponível"). As outras falhas inesperadas continuam 500. O Warning leva só o que identifica a falha e a
/// requisição: a mensagem do Npgsql pode trazer host, porta e usuário, então nem ela nem a exceção vão para o log.
/// </summary>
internal sealed partial class DatabaseUnavailableExceptionHandler(ILogger<DatabaseUnavailableExceptionHandler> logger)
    : IExceptionHandler
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (DatabaseUnavailability.TemporaryCause(exception) is not { } cause)
        {
            return false;
        }

        LogDatabaseUnavailable(
            logger,
            (cause as PostgresException)?.SqlState,
            cause.GetType().Name,
            RouteOf(httpContext),
            Activity.Current?.Id ?? httpContext.TraceIdentifier);
        ApiProblems.SetRetryAfter(httpContext, RetryAfter);
        await ApiProblems.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiProblems.TemporarilyUnavailable)
            .ExecuteAsync(httpContext);
        return true;
    }

    /// <summary>O padrão da rota (sem ids); o middleware de exceção já limpou o endpoint do contexto.</summary>
    private static string? RouteOf(HttpContext httpContext)
    {
        var failure = httpContext.Features.Get<IExceptionHandlerFeature>();
        return (failure?.Endpoint as RouteEndpoint)?.RoutePattern.RawText ?? failure?.Path;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Database unavailable ({SqlState}, {ErrorType}) on {Route}, trace {TraceId}")]
    private static partial void LogDatabaseUnavailable(
        ILogger logger, string? sqlState, string errorType, string? route, string traceId);
}
