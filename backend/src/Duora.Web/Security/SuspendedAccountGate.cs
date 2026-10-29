using Duora.Web.Http;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Duora.Web.Security;

/// <summary>Marca a rota que a sessão de conta suspensa ainda alcança (spec 8.2; issue #299).</summary>
internal sealed class SuspendedAccountAllowedEndpoint;

/// <summary>
/// Conta suspensa entra, mas a sessão só alcança o estado da própria conta, a segurança dela e o recurso (spec 8.2):
/// negar por padrão. Toda rota com sessão responde 403 <c>ACCOUNT_SUSPENDED</c> à conta suspensa, salvo as marcadas
/// com <see cref="SuspendedAccountAccess.AllowForSuspendedAccount{TBuilder}"/>. Rota pública (<c>AllowAnonymous</c>)
/// não muda: ela já atende quem não tem sessão. O estado vem do banco a cada requisição (ticket store), então a suspensão vale já na
/// requisição seguinte de toda sessão aberta, e a revogação devolve o acesso do mesmo jeito.
/// </summary>
/// <remarks>
/// Roda depois da autenticação e antes da autorização: sem sessão continua 401, e o 403 da suspensão vem antes do de
/// elegibilidade ou de papel, para a SPA mostrar a tela da conta suspensa em qualquer rota.
/// </remarks>
internal sealed class SuspendedAccountGate(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null
            || DuoraClaims.ReadSession(context.User) is not { AccountIsSuspended: true }
            || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<SuspendedAccountAllowedEndpoint>() is not null)
        {
            return next(context);
        }

        return ApiProblems.Problem(StatusCodes.Status403Forbidden, ApiProblems.AccountSuspended).ExecuteAsync(context);
    }
}

internal static class SuspendedAccountAccess
{
    /// <summary>Allowlist da conta suspensa: estado da própria conta, sessões, MFA, senha e reautenticação.</summary>
    public static TBuilder AllowForSuspendedAccount<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new SuspendedAccountAllowedEndpoint());

    public static IApplicationBuilder UseSuspendedAccountGate(this IApplicationBuilder app) =>
        app.UseMiddleware<SuspendedAccountGate>();
}
