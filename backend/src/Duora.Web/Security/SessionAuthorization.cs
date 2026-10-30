using Duora.Web.Http;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Duora.Web.Security;

/// <summary>
/// Autorização pela sessão (ADR-0017; auth.md, "MFA"). Negar por padrão: toda rota sem política própria é de
/// participante e exige sessão de conta não privilegiada (decisão I2 da revisão do #38: moderação e administração
/// usam contas próprias e não alcançam perfil, bloqueio nem salas). Conta privilegiada sem segundo fator só alcança
/// as rotas marcadas com <see cref="AllowDuringMfaEnrollment{TBuilder}"/>; as demais respondem 403
/// <c>MFA_ENROLLMENT_REQUIRED</c>. Rotas da própria conta (sessões, MFA, senha) usam
/// <see cref="RequireAccountSession{TBuilder}"/>, e as <c>/admin</c> exigem a sessão administrativa completa. Antes de
/// tudo isso, conta com a senha temporária do bootstrap (D4) só alcança a troca de senha
/// (<see cref="AllowDuringPasswordChange{TBuilder}"/>); as demais respondem 403 <c>PASSWORD_CHANGE_REQUIRED</c>.
/// </summary>
internal static class SessionAuthorization
{
    /// <summary>Sessão qualquer, inclusive de conta privilegiada que ainda cadastra o MFA.</summary>
    public const string SignedIn = "signed-in";

    /// <summary>Qualquer conta com o MFA exigido já provado: rotas da própria conta.</summary>
    public const string AccountSession = "account-session";

    /// <summary>Conta privilegiada, sessão administrativa e MFA provado nesta sessão.</summary>
    public const string AdministrativeSession = "administrative-session";

    /// <summary>
    /// Troca de senha (API-A16): rota da própria conta que também atende a sessão aberta com a senha temporária,
    /// mas não a conta privilegiada que ainda cadastra o MFA.
    /// </summary>
    public const string PasswordChange = "password-change";

    public static IServiceCollection AddSessionAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, SessionRequirementsHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, SessionAuthorizationResultHandler>();
        var accountSession = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PasswordChangeCompleteRequirement(), new MfaEnrollmentCompleteRequirement())
            .Build();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(
                    new PasswordChangeCompleteRequirement(),
                    new MfaEnrollmentCompleteRequirement(),
                    new ParticipantAccountRequirement())
                .Build())
            // RequireAuthorization() sem nome não pode abrir mão do MFA da conta privilegiada.
            .SetDefaultPolicy(accountSession)
            .AddPolicy(AccountSession, accountSession)
            .AddPolicy(SignedIn, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PasswordChangeCompleteRequirement()))
            .AddPolicy(PasswordChange, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PasswordChangeAllowedRequirement()))
            .AddPolicy(AdministrativeSession, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(
                    new PasswordChangeCompleteRequirement(),
                    new MfaEnrollmentCompleteRequirement(),
                    new AdministrativeSessionRequirement()));
        return services;
    }

    /// <summary>
    /// Allowlist da sessão restrita de conta privilegiada sem MFA: só o necessário para cadastrá-lo (A08, A11, A12
    /// e a reautenticação A13); logout e as rotas públicas já não exigem sessão.
    /// </summary>
    public static TBuilder AllowDuringMfaEnrollment<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(SignedIn);

    /// <summary>Única rota da sessão aberta com a senha temporária do bootstrap: a troca de senha (API-A16).</summary>
    public static TBuilder AllowDuringPasswordChange<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PasswordChange);

    /// <summary>Rota da própria conta, aberta também a conta privilegiada (com o MFA dela provado).</summary>
    public static TBuilder RequireAccountSession<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(AccountSession);
}

internal sealed class MfaEnrollmentCompleteRequirement : IAuthorizationRequirement;

/// <summary>A conta não usa mais a senha temporária do bootstrap.</summary>
internal sealed class PasswordChangeCompleteRequirement : IAuthorizationRequirement;

/// <summary>Pode trocar a senha: está na senha temporária ou já não deve o MFA da conta privilegiada.</summary>
internal sealed class PasswordChangeAllowedRequirement : IAuthorizationRequirement;

internal sealed class AdministrativeSessionRequirement : IAuthorizationRequirement;

internal sealed class ParticipantAccountRequirement : IAuthorizationRequirement;

internal sealed class SessionRequirementsHandler : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (DuoraClaims.ReadSession(context.User) is not { } session)
        {
            // Sem sessão: o RequireAuthenticatedUser já falha e o resultado é 401.
            return Task.CompletedTask;
        }

        foreach (var requirement in context.PendingRequirements.ToList())
        {
            switch (requirement)
            {
                case PasswordChangeCompleteRequirement when session.AwaitsPasswordChange:
                    context.Fail(new AuthorizationFailureReason(this, ApiProblems.PasswordChangeRequired));
                    break;
                case PasswordChangeCompleteRequirement:
                    context.Succeed(requirement);
                    break;
                case PasswordChangeAllowedRequirement when session.AwaitsPasswordChange || !session.AwaitsMfaEnrollment:
                    context.Succeed(requirement);
                    break;
                case PasswordChangeAllowedRequirement:
                    context.Fail(new AuthorizationFailureReason(this, ApiProblems.MfaEnrollmentRequired));
                    break;
                case MfaEnrollmentCompleteRequirement when session.AwaitsMfaEnrollment:
                    context.Fail(new AuthorizationFailureReason(this, ApiProblems.MfaEnrollmentRequired));
                    break;
                case MfaEnrollmentCompleteRequirement:
                    context.Succeed(requirement);
                    break;
                case AdministrativeSessionRequirement when session.IsAdministrative:
                    context.Succeed(requirement);
                    break;
                case ParticipantAccountRequirement when !session.AccountIsPrivileged:
                    context.Succeed(requirement);
                    break;
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Dá aos 403 de senha temporária e de MFA pendente os códigos próprios e à elegibilidade indisponível o 503; os
/// outros resultados seguem o tratamento padrão. A senha temporária vem primeiro: a conta nessa fase também deve o
/// MFA, e o que ela precisa fazer agora é trocar a senha.
/// </summary>
internal sealed class SessionAuthorizationResultHandler(IAntiforgery antiforgery) : IAuthorizationMiddlewareResultHandler
{
    private static readonly TimeSpan EligibilityRetryAfter = TimeSpan.FromSeconds(5);

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        var awaitsPasswordChange = authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.Any(
                reason => reason.Message == ApiProblems.PasswordChangeRequired) == true;
        var awaitsMfa = authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.Any(
                reason => reason.Message == ApiProblems.MfaEnrollmentRequired) == true;
        var eligibilityUnavailable = authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.OfType<EligibilityUnavailableReason>().Any() == true;
        if (eligibilityUnavailable)
        {
            // Sem banco, a elegibilidade não é conhecida: 503 para o cliente tentar de novo, nunca 403 (ADR-0024).
            ApiProblems.SetRetryAfter(context, EligibilityRetryAfter);
            await ApiProblems.Problem(
                    StatusCodes.Status503ServiceUnavailable,
                    ApiProblems.TemporarilyUnavailable)
                .ExecuteAsync(context);
            return;
        }

        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.OfType<AccountModerationForbiddenReason>().Any() == true)
        {
            await ApiProblems.Problem(StatusCodes.Status403Forbidden, ApiProblems.AccountModerationForbidden)
                .ExecuteAsync(context);
            return;
        }

        if (awaitsPasswordChange)
        {
            var problem = ApiProblems.Problem(StatusCodes.Status403Forbidden, ApiProblems.PasswordChangeRequired);
            if (HttpMethods.IsGet(context.Request.Method)
                && context.GetEndpoint()?.Metadata.GetMetadata<SessionContextBootstrapEndpoint>() is not null)
            {
                problem.ProblemDetails.Extensions["sessionContext"] = PrivateOperationContext.Issue(context, antiforgery);
            }
            await problem.ExecuteAsync(context);
            return;
        }

        if (awaitsMfa)
        {
            await ApiProblems.Problem(
                    StatusCodes.Status403Forbidden,
                    ApiProblems.MfaEnrollmentRequired)
                .ExecuteAsync(context);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
