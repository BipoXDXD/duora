using Duora.Contracts.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Duora.Web.Accounts;

/// <summary>Conta e sessões do titular (API-A08 a API-A10). Exigem sessão pela política padrão de autorização.</summary>
internal static class MeEndpoints
{
    public static void MapMeEndpoints(this IEndpointRouteBuilder api)
    {
        var me = api.MapGroup("/me").WithTags("Me");
        var sessions = me.MapGroup("/sessions").RequireAccountSession();

        me.MapGet("", GetMeAsync)
            .AllowDuringMfaEnrollment()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        sessions.MapGet("", ListSessionsAsync).ProducesProblem(StatusCodes.Status401Unauthorized);
        sessions.MapDelete("/{sessionId:guid}", RevokeSessionAsync)
            .RequireAccountRateLimit(AccountIdOperation.SessionRevocation)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> GetMeAsync(
        HttpContext context,
        AccountService accounts,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId
            || await accounts.FindAccountAsync(userId, cancellationToken) is not { } account)
        {
            return TypedResults.Unauthorized();
        }

        return TypedResults.Ok(new MeResponse(
            account.Id,
            account.Email,
            account.Status.ToString(),
            account.EmailConfirmed,
            account.MfaEnabled,
            account.Roles));
    }

    private static async Task<Results<Ok<SessionListResponse>, UnauthorizedHttpResult>> ListSessionsAsync(
        HttpContext context,
        LoginSessions sessions,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var currentSessionId = DuoraClaims.ReadSessionId(context.User);
        var active = await sessions.ListActiveAsync(userId, cancellationToken);
        var items = active
            .Select(session => new SessionResponse(
                session.Id,
                session.CreatedAt.UtcDateTime,
                session.LastSeenAt.UtcDateTime,
                session.Id == currentSessionId,
                session.Description))
            .ToList();
        return TypedResults.Ok(new SessionListResponse(items));
    }

    /// <summary>Sessão de outra pessoa e sessão inexistente dão o mesmo 404 (spec 8.1).</summary>
    private static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> RevokeSessionAsync(
        Guid sessionId,
        HttpContext context,
        AccountSecurityService security,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        return await security.RevokeOwnSessionAsync(userId, sessionId, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }
}
