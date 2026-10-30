using System.Data.Common;
using Duora.Infrastructure.Safety;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Duora.Web.Security;

/// <summary>
/// Permissão específica da moderação de conta (ADR-0042): papel <c>moderator</c>, lido do banco a cada requisição.
/// É policy, e não filtro de endpoint, para negar no middleware de autorização, antes do binding do corpo: quem não
/// modera recebe 403 sem que o servidor valide JSON, campos ou <c>Idempotency-Key</c>. Os comandos conferem de novo,
/// ao vivo, na própria transação.
/// </summary>
internal static class AccountModerationPolicy
{
    public const string Name = "account-moderator";

    public static AuthorizationBuilder AddAccountModerator(this AuthorizationBuilder authorization) =>
        authorization.AddPolicy(Name, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new AccountModeratorRequirement()));
}

internal sealed class AccountModeratorRequirement : IAuthorizationRequirement;

internal sealed class AccountModeratorHandler(AccountSuspensions suspensions)
    : AuthorizationHandler<AccountModeratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AccountModeratorRequirement requirement)
    {
        // Sem sessão administrativa, a policy do grupo /admin já nega com FORBIDDEN; aqui não há o que acrescentar.
        if (DuoraClaims.ReadSession(context.User) is not { IsAdministrative: true }
            || DuoraClaims.ReadUserId(context.User) is not { } userId
            || DuoraClaims.ReadSessionId(context.User) is not { } sessionId)
        {
            return;
        }

        var cancellationToken = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        bool moderator;
        try
        {
            moderator = await suspensions.IsModeratorSessionAsync(new ModeratorSession(userId, sessionId), cancellationToken);
        }
        catch (Exception exception) when (IsDatabaseUnavailable(exception))
        {
            // Sem banco não se sabe: falha fechada como indisponibilidade (503), não como proibição.
            context.Fail(new EligibilityUnavailableReason(this));
            return;
        }

        if (moderator)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AccountModerationForbiddenReason(this));
        }
    }

    /// <summary>O EF embrulha a falha de conexão; vale qualquer <see cref="DbException"/> na cadeia.</summary>
    private static bool IsDatabaseUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Sessão administrativa sem o papel <c>moderator</c>: 403 <c>ACCOUNT_MODERATION_FORBIDDEN</c>.</summary>
internal sealed class AccountModerationForbiddenReason(IAuthorizationHandler handler)
    : AuthorizationFailureReason(handler, "Moderação de conta exige o papel moderator.");
