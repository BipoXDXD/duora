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
    public static bool IsTemporary(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case PostgresException server when IsUnavailableState(server.SqlState):
                    return true;
                case PostgresException:
                    // Erro de SQL comum (constraint, sintaxe, serialização): não é indisponibilidade.
                    return false;
                case NpgsqlException client when client.IsTransient:
                    // Falha de rede, conexão recusada ou pool esgotado: o servidor nem respondeu.
                    return true;
            }
        }

        return false;
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
/// "Banco indisponível"). As outras falhas inesperadas continuam 500.
/// </summary>
internal sealed class DatabaseUnavailableExceptionHandler : IExceptionHandler
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!DatabaseUnavailability.IsTemporary(exception))
        {
            return false;
        }

        ApiProblems.SetRetryAfter(httpContext, RetryAfter);
        await ApiProblems.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiProblems.TemporarilyUnavailable,
                "Serviço temporariamente indisponível. Tente novamente em instantes.")
            .ExecuteAsync(httpContext);
        return true;
    }
}
