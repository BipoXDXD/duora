using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace Duora.Web.Http;

/// <summary>
/// Banco temporariamente fora do alcance: conexão recusada ou caída, pool esgotado (o <c>Timeout</c> do pool venceu)
/// ou servidor sem conexões livres (53300), desligando (57P01 a 57P03) ou com a conexão perdida (08000, 08001, 08003,
/// 08004, 08006 e 08007). Também sete estados de recurso
/// do servidor, crus como embrulhados: 53000 (recursos insuficientes), 53100 (disco cheio), 53200 (sem memória), 53400
/// (limite de configuração), 55006 (objeto em uso), 57P05 (sessão ociosa encerrada) e 58030 (erro de E/S) (#476,
/// #492). A lista é própria, sem paridade com o <c>IsTransient</c> do Npgsql: 55000 (defeito costuma ser permanente)
/// e 58000 (genérico demais) ficam de fora de propósito e continuam 500 quando crus.
/// E a contenção, que passa sozinha: serialização (40001), deadlock (40P01) e <c>lock_timeout</c> (55P03), crua ou
/// embrulhada pelo Npgsql (#465). O EF costuma embrulhar a causa numa <see cref="InvalidOperationException"/>
/// ("transient failure"), então a cadeia inteira é examinada.
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
                case PostgresException server when IsUnavailableState(server.SqlState) || IsContention(server):
                    return server;
                case PostgresException:
                    // Erro de SQL comum (constraint, sintaxe): não é indisponibilidade.
                    return null;
                case NpgsqlException client when client.IsTransient:
                    // Falha de rede, conexão recusada ou pool esgotado: o servidor nem respondeu. Ou um erro do
                    // servidor que o Npgsql embrulhou e considera passageiro: o erro do servidor é a causa, para o
                    // Warning levar o SqlState dele e a contenção embrulhada responder igual à crua.
                    return ServerErrorWrappedBy(client) ?? client;
            }
        }

        return null;
    }

    // O IsTransient da NpgsqlException vem da interna imediata e, se ela também é NpgsqlException, desce por ela até
    // a primeira interna de outro tipo (comportamento observado no Npgsql 10.0.3). Se essa interna é o erro do
    // servidor, foi o IsTransient dele que decidiu; se é outra (um timeout do cliente, por exemplo), um
    // PostgresException mais fundo não é a causa.
    private static PostgresException? ServerErrorWrappedBy(NpgsqlException client)
    {
        var inner = client.InnerException;
        while (inner is NpgsqlException and not PostgresException)
        {
            inner = inner.InnerException;
        }

        return inner as PostgresException;
    }

    // Passa sozinho em instantes: os lock_timeout e statement_timeout curtos do projeto ficam em 2 s e os 503 de
    // contenção já respondiam 2, então outra tentativa nesse prazo costuma passar.
    private static readonly TimeSpan ContentionRetryAfter = TimeSpan.FromSeconds(2);

    // Banco fora do alcance (rede, pool, servidor cheio ou desligando): recuperar leva mais tempo.
    private static readonly TimeSpan UnreachableRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// O 503 de banco indisponível, com o <c>Retry-After</c> da causa: fonte única para o handler global, a
    /// elegibilidade (#473) e os endpoints que respondem sem o handler (#493). Contenção e timeout de comando (57014)
    /// passam logo; sem causa conhecida (<paramref name="cause"/> nulo), vale o de banco fora do alcance. O 57014 só
    /// chega aqui pelos filtros de mídia e revisão de foto; o handler global o trata como indisponibilidade só nas
    /// rotas neutras, onde o valor do <c>Retry-After</c> é o da conta ocupada.
    /// </summary>
    public static ProblemHttpResult Unavailable(HttpContext httpContext, NpgsqlException? cause) =>
        ApiProblems.Unavailable(httpContext, cause is not null && RecoversSoon(cause) ? ContentionRetryAfter : UnreachableRetryAfter);

    /// <summary>
    /// O 503 de contenção que esgotou as tentativas, quando o resultado do serviço já não carrega a exceção
    /// (<c>Busy</c>): o mesmo <c>Retry-After</c> da contenção crua.
    /// </summary>
    public static ProblemHttpResult Contention(HttpContext httpContext) =>
        ApiProblems.Unavailable(httpContext, ContentionRetryAfter);

    private static bool RecoversSoon(NpgsqlException cause) =>
        IsContention(cause) || cause is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled };

    /// <summary>Contenção entre transações: a mesma operação costuma passar logo em seguida.</summary>
    public static bool IsContention(NpgsqlException cause) =>
        cause is PostgresException
        {
            SqlState: PostgresErrorCodes.SerializationFailure
                or PostgresErrorCodes.DeadlockDetected
                or PostgresErrorCodes.LockNotAvailable,
        };

    // Estados de recurso do servidor tratados como indisponibilidade, além de 53300 e do desligamento: 53000 (recursos
    // insuficientes), 53100 (disco cheio), 53200 (sem memória), 53400 (limite de configuração), 55006 (objeto em uso),
    // 57P05 (sessão ociosa encerrada) e 58030 (erro de E/S). Não é o conjunto do IsTransient do
    // Npgsql: 55000 e 58000, que ele cobre, continuam 500 quando crus.
    // Da classe 08, lista explícita e não o prefixo: é a do IsTransient do Npgsql 10.0.3 (08000, 08001, 08003, 08004,
    // 08006 e 08007). 08P01 (protocol_violation) é defeito de código ou de proxy, como um bind com número de
    // parâmetros errado, e 08002 (connection_already_exists) não é a rede fora: seguem 500 quando crus (#497).
    private static bool IsUnavailableState(string sqlState) =>
        sqlState is PostgresErrorCodes.TooManyConnections
            or PostgresErrorCodes.InsufficientResources
            or PostgresErrorCodes.DiskFull
            or PostgresErrorCodes.IdleSessionTimeout
            or PostgresErrorCodes.OutOfMemory
            or PostgresErrorCodes.ConfigurationLimitExceeded
            or PostgresErrorCodes.ObjectInUse
            or PostgresErrorCodes.IoError
            or PostgresErrorCodes.AdminShutdown
            or PostgresErrorCodes.CrashShutdown
            or PostgresErrorCodes.CannotConnectNow
            or PostgresErrorCodes.ConnectionException
            or PostgresErrorCodes.SqlClientUnableToEstablishSqlConnection
            or PostgresErrorCodes.ConnectionDoesNotExist
            or PostgresErrorCodes.SqlServerRejectedEstablishmentOfSqlConnection
            or PostgresErrorCodes.ConnectionFailure
            or PostgresErrorCodes.TransactionResolutionUnknown;
}

/// <summary>
/// Banco indisponível vira 503 <c>TEMPORARILY_UNAVAILABLE</c> com <c>Retry-After</c>, sem detalhe (spec 11; auth.md,
/// "Banco indisponível"), em qualquer rota: nenhum filtro de endpoint trata contenção por conta própria (#465). As
/// outras falhas inesperadas continuam 500. O Warning leva só o que identifica a falha e a requisição: a mensagem do
/// Npgsql pode trazer host, porta e usuário, então nem ela nem a exceção vão para o log.
/// </summary>
internal sealed partial class DatabaseUnavailableExceptionHandler(ILogger<DatabaseUnavailableExceptionHandler> logger)
    : IExceptionHandler
{
    private const string NoSqlState = "-";

    // Sem endpoint, o path é texto do cliente e sem limite de tamanho: não vai para o log.
    private const string NoEndpoint = "(no endpoint)";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        if (CauseFor(httpContext, exception) is not { } cause)
        {
            return false;
        }

        var contention = DatabaseUnavailability.IsContention(cause);
        Log(contention, cause, httpContext);
        await DatabaseUnavailability.Unavailable(httpContext, cause).ExecuteAsync(httpContext);
        return true;
    }

    // Nas rotas de indisponibilidade neutra, o timeout de comando (57014) também é indisponibilidade: lá todo 503 é
    // igual, e um 500 nesse caso destoaria das outras causas. Fora delas, 57014 continua com quem o espera (filtros de
    // mídia e revisão de foto) ou vira 500.
    private static NpgsqlException? CauseFor(HttpContext httpContext, Exception exception) =>
        DatabaseUnavailability.TemporaryCause(exception)
        ?? (ApiProblems.IsNeutralUnavailability(httpContext) ? QueryCanceledIn(exception) : null);

    private static PostgresException? QueryCanceledIn(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled } canceled)
            {
                return canceled;
            }
        }

        return null;
    }

    /// <summary>O Warning separa contenção de banco fora pelo mesmo critério do <c>Retry-After</c>.</summary>
    private void Log(bool contention, NpgsqlException cause, HttpContext httpContext)
    {
        var sqlState = (cause as PostgresException)?.SqlState ?? NoSqlState;
        var errorType = cause.GetType().Name;
        var route = RouteOf(httpContext);
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        if (contention)
        {
            LogDatabaseContention(logger, sqlState, errorType, route, traceId);
        }
        else
        {
            LogDatabaseUnavailable(logger, sqlState, errorType, route, traceId);
        }
    }

    /// <summary>O padrão da rota (sem ids); o middleware de exceção já limpou o endpoint do contexto.</summary>
    private static string RouteOf(HttpContext httpContext)
    {
        var failure = httpContext.Features.Get<IExceptionHandlerFeature>();
        return (failure?.Endpoint as RouteEndpoint)?.RoutePattern.RawText ?? NoEndpoint;
    }

    [LoggerMessage(EventId = 1, EventName = "DatabaseUnavailable", Level = LogLevel.Warning,
        Message = "Database unavailable ({SqlState}, {ErrorType}) on {Route}, trace {TraceId}")]
    private static partial void LogDatabaseUnavailable(
        ILogger logger, string sqlState, string errorType, string route, string traceId);

    [LoggerMessage(EventId = 2, EventName = "DatabaseContention", Level = LogLevel.Warning,
        Message = "Database contention ({SqlState}, {ErrorType}) on {Route}, trace {TraceId}")]
    private static partial void LogDatabaseContention(
        ILogger logger, string sqlState, string errorType, string route, string traceId);
}
