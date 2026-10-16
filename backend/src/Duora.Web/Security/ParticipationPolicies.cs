using Duora.Infrastructure.Identity;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Duora.Web.Security;

/// <summary>
/// Policies de participação (INV-01, T-01; ADR-0024). Toda rota de evento, sala, decisão, conversa ou perfil alheio
/// exige <see cref="EligibleParticipant"/>; as rotas de conta, sessão e verificação ficam fora, porque o titular
/// precisa delas justamente enquanto não é elegível (spec 8.2).
/// </summary>
internal static class ParticipationPolicies
{
    public const string EligibleParticipant = "EligibleParticipant";

    public static void AddEligibleParticipant(this AuthorizationBuilder authorization) =>
        authorization.AddPolicy(EligibleParticipant, policy => policy
            .RequireAuthenticatedUser()
            // Os mesmos requisitos da política padrão de participante (SessionAuthorization), mais a elegibilidade.
            .AddRequirements(
                new MfaEnrollmentCompleteRequirement(),
                new ParticipantAccountRequirement(),
                new EligibleParticipantRequirement()));

    /// <summary>Para os grupos de rotas de participação dos módulos.</summary>
    public static TBuilder RequireEligibleParticipant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(EligibleParticipant);
}

/// <summary>Conta ativa com verificação etária adulta dentro da validade, avaliada a cada requisição no banco.</summary>
internal sealed class EligibleParticipantRequirement : IAuthorizationRequirement;

/// <summary>
/// Pergunta ao Identity (<c>GetEligibility</c>) a cada requisição, sem cache: restrição, suspensão e vencimento da
/// verificação valem na requisição seguinte. A sessão vai junto, sem efeito por ora (ADR-0024, decisão 3).
/// </summary>
internal sealed class EligibleParticipantHandler(AgeVerificationService verifications)
    : AuthorizationHandler<EligibleParticipantRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        EligibleParticipantRequirement requirement)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId
            || DuoraClaims.ReadSessionId(context.User) is not { } sessionId)
        {
            return;
        }

        var cancellationToken = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        var eligibility = await verifications.GetEligibilityAsync(userId, sessionId, cancellationToken);
        if (eligibility.IsEligible)
        {
            context.Succeed(requirement);
        }
    }
}
