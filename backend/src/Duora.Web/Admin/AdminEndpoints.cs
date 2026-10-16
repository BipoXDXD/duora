using Duora.Web.Security;

namespace Duora.Web.Admin;

/// <summary>
/// Rotas de um módulo administrativo, penduradas no grupo <c>/api/v1/admin</c>. Quem registra só declara as rotas:
/// a sessão administrativa (conta privilegiada, política administrativa e MFA provado) vem do grupo, e não há como
/// esquecê-la. As ações críticas acrescentam <see cref="StepUp.RequireRecentMfa{TBuilder}"/>.
/// </summary>
internal sealed record AdminRoutes(Action<RouteGroupBuilder> Map);

internal static class AdminEndpoints
{
    /// <summary>
    /// Grupo <c>/admin</c> (spec 8.5) com a política <see cref="SessionAuthorization.AdministrativeSession"/>. Neste
    /// PR ele nasce sem rotas de produto: as API-ADM chegam com seus módulos, cada uma com a permissão específica
    /// dela por cima desta política.
    /// </summary>
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder api, IEnumerable<AdminRoutes> modules)
    {
        var admin = api.MapGroup("/admin")
            .WithTags("Admin")
            .RequireAuthorization(SessionAuthorization.AdministrativeSession);
        foreach (var module in modules)
        {
            module.Map(admin);
        }

        return admin;
    }
}
