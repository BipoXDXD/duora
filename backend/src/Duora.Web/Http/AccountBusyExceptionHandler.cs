using System.Diagnostics;
using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Diagnostics;

namespace Duora.Web.Http;

/// <summary>
/// Conta travada por outra verificação além do teto (55P03): 503 <c>TEMPORARILY_UNAVAILABLE</c> com
/// <c>Retry-After</c>, como o bloqueio de pessoas faz na contenção (BlockEndpoints). Vale para toda rota que trava a
/// conta ou a sessão (A04, A05, A07, A10, A11 a A16) sem repetir o tratamento em cada uma.
/// </summary>
/// <remarks>
/// A resposta é a mesma do banco indisponível (salvo o <c>Retry-After</c>): dizer ao cliente que a conta está ocupada
/// confirmaria que ela existe. A distinção fica só no Warning e na métrica, pelo motivo interno. O Warning não leva a
/// exceção nem a mensagem dela, que um chamador poderia preencher com e-mail ou id.
/// </remarks>
internal sealed partial class AccountBusyExceptionHandler(
    ILogger<AccountBusyExceptionHandler> logger,
    AccountBusyMetrics metrics) : IExceptionHandler
{
    // Sem endpoint, o path é texto do cliente e sem limite de tamanho: não vai para o log.
    private const string NoEndpoint = "(no endpoint)";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AccountBusyException busy)
        {
            return false;
        }

        LogAccountBusy(
            logger,
            busy.Reason,
            RouteOf(httpContext),
            (long)busy.WaitCeiling.TotalMilliseconds,
            Activity.Current?.Id ?? httpContext.TraceIdentifier);
        metrics.Record(busy.Reason);
        ApiProblems.SetRetryAfter(httpContext, busy.RetryAfter);
        await ApiProblems.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiProblems.TemporarilyUnavailable)
            .ExecuteAsync(httpContext);
        return true;
    }

    /// <summary>O padrão da rota (sem ids); o middleware de exceção já limpou o endpoint do contexto.</summary>
    private static string RouteOf(HttpContext httpContext)
    {
        var failure = httpContext.Features.Get<IExceptionHandlerFeature>();
        return (failure?.Endpoint as RouteEndpoint)?.RoutePattern.RawText ?? NoEndpoint;
    }

    // Warning, como o do banco indisponível: o cliente recebeu um 503. Conta ocupada isolada é contenção normal, mas
    // em série indica fila travada, lock longo ou rajada contra uma conta; a métrica conta, o log dá rota e trace.
    [LoggerMessage(EventId = 5031, EventName = "AccountBusy", Level = LogLevel.Warning,
        Message = "Account busy ({Reason}) on {Route}, ceiling {CeilingMilliseconds} ms, trace {TraceId}")]
    private static partial void LogAccountBusy(
        ILogger logger, AccountBusyReason reason, string route, long ceilingMilliseconds, string traceId);
}
