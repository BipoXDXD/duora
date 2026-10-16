using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Diagnostics;

namespace Duora.Web.Http;

/// <summary>
/// Conta travada por outra verificação além do teto (55P03): 503 <c>TEMPORARILY_UNAVAILABLE</c> com
/// <c>Retry-After</c>, como o bloqueio de pessoas faz na contenção (BlockEndpoints). Vale para toda rota que trava a
/// conta (A04, A11 a A16) sem repetir o tratamento em cada uma.
/// </summary>
internal sealed class AccountBusyExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AccountBusyException busy)
        {
            return false;
        }

        ApiProblems.SetRetryAfter(httpContext, busy.RetryAfter);
        await ApiProblems.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiProblems.TemporarilyUnavailable,
                "A conta está ocupada. Tente novamente em instantes.")
            .ExecuteAsync(httpContext);
        return true;
    }
}
