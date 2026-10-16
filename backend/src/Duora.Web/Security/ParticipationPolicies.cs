using System.Data.Common;
using Duora.Core.Identity;
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

    /// <summary>
    /// Rota de participação mínima, só no ambiente <c>Testing</c>: enquanto nenhum módulo tem rota de participação, é
    /// ela que prova, por HTTP, que a policy nega e libera (T-01) e que a enumeração das rotas não está vazia.
    /// </summary>
    public const string TestSentinelPath = "/test/eligible-participant";

    public static void MapParticipationTestSentinel(this IEndpointRouteBuilder api, IHostEnvironment environment)
    {
        if (environment.IsEnvironment("Testing"))
        {
            api.MapGet(TestSentinelPath, () => TypedResults.Ok()).RequireEligibleParticipant().ExcludeFromDescription();
        }
    }

    /// <summary>Para os grupos de rotas de participação dos módulos.</summary>
    public static TBuilder RequireEligibleParticipant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(EligibleParticipant);
}

/// <summary>Conta ativa com verificação etária adulta (que não vence), avaliada a cada requisição no banco.</summary>
internal sealed class EligibleParticipantRequirement : IAuthorizationRequirement;

/// <summary>
/// Pergunta ao Identity (<c>GetEligibility</c>) a cada requisição, sem cache: restrição e suspensão valem na
/// requisição seguinte. A sessão vai junto, sem efeito por ora (ADR-0024, decisão 3).
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
        AgeEligibilityResult eligibility;
        try
        {
            eligibility = await verifications.GetEligibilityAsync(userId, sessionId, cancellationToken);
        }
        catch (Exception exception) when (IsDatabaseUnavailable(exception))
        {
            // Sem banco não se sabe: falha fechada, mas como indisponibilidade (503), não como proibição (M7).
            context.Fail(new EligibilityUnavailableReason(this));
            return;
        }

        if (eligibility.IsEligible)
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>O EF embrulha a falha de conexão (estratégia de execução); vale qualquer <see cref="DbException"/> na cadeia.</summary>
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

/// <summary>A elegibilidade não pôde ser consultada (banco fora do ar).</summary>
internal sealed class EligibilityUnavailableReason(IAuthorizationHandler handler)
    : AuthorizationFailureReason(handler, "Elegibilidade indisponível.");
