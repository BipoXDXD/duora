using Duora.Web.Http;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Duora.Web.Security;

/// <summary>
/// Autorização pela sessão (ADR-0017; auth.md, "MFA"). Negar por padrão: toda rota sem política própria exige sessão
/// e, se a conta é privilegiada, o MFA provado nesta sessão. Conta privilegiada sem segundo fator só alcança as
/// rotas marcadas com <see cref="AllowDuringMfaEnrollment{TBuilder}"/>; as demais respondem 403
/// <c>MFA_ENROLLMENT_REQUIRED</c>. As rotas <c>/admin</c> exigem a sessão administrativa completa.
/// </summary>
internal static class SessionAuthorization
{
    /// <summary>Sessão qualquer, inclusive de conta privilegiada que ainda cadastra o MFA.</summary>
    public const string SignedIn = "signed-in";

    /// <summary>Conta privilegiada, sessão administrativa e MFA provado nesta sessão.</summary>
    public const string AdministrativeSession = "administrative-session";

    public static IServiceCollection AddSessionAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, SessionRequirementsHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, SessionAuthorizationResultHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new MfaEnrollmentCompleteRequirement())
                .Build())
            .AddPolicy(SignedIn, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(AdministrativeSession, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new MfaEnrollmentCompleteRequirement(), new AdministrativeSessionRequirement()));
        return services;
    }

    /// <summary>
    /// Allowlist da sessão restrita de conta privilegiada sem MFA: só o necessário para cadastrá-lo (A08, A11, A12
    /// e a reautenticação A13); logout e as rotas públicas já não exigem sessão.
    /// </summary>
    public static TBuilder AllowDuringMfaEnrollment<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(SignedIn);
}

internal sealed class MfaEnrollmentCompleteRequirement : IAuthorizationRequirement;

internal sealed class AdministrativeSessionRequirement : IAuthorizationRequirement;

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
                case MfaEnrollmentCompleteRequirement when session.AwaitsMfaEnrollment:
                    context.Fail(new AuthorizationFailureReason(this, ApiProblems.MfaEnrollmentRequired));
                    break;
                case MfaEnrollmentCompleteRequirement:
                    context.Succeed(requirement);
                    break;
                case AdministrativeSessionRequirement when session.IsAdministrative:
                    context.Succeed(requirement);
                    break;
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>Dá ao 403 de MFA pendente o código próprio; os outros resultados seguem o tratamento padrão.</summary>
internal sealed class SessionAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        var awaitsMfa = authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.Any(
                reason => reason.Message == ApiProblems.MfaEnrollmentRequired) == true;
        if (awaitsMfa)
        {
            await ApiProblems.Problem(
                    StatusCodes.Status403Forbidden,
                    ApiProblems.MfaEnrollmentRequired,
                    "Cadastre o segundo fator para continuar.")
                .ExecuteAsync(context);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
