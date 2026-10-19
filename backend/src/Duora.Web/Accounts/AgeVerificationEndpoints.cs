using Duora.Contracts.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Http;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Duora.Web.Accounts;

/// <summary>
/// Verificação etária do titular (API-P05, API-P06; ADR-0024). Exigem sessão pela política padrão. O pedido só cria
/// a solicitação e a tarefa do worker; o fornecedor nunca é chamado no request.
/// </summary>
internal static class AgeVerificationEndpoints
{
    public const string CurrentPath = "/api/v1/me/age-verifications/current";

    public static void MapAgeVerificationEndpoints(this IEndpointRouteBuilder api)
    {
        var verifications = api.MapGroup("/me/age-verifications").WithTags("Me");

        verifications.MapPost("", StartAsync)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        verifications.MapGet("/current", GetCurrentAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Cria a solicitação ou devolve a aberta (202 nos dois casos, idempotência natural), inclusive a que está em revisão
    /// manual (<c>WaitForReview</c>). 409 quando a conta não pode pedir: elegível, rejeitada, restrita ou suspensa,
    /// mesmo com uma aberta; 503 sem fornecedor.
    /// </summary>
    private static async Task<Results<Accepted<AgeVerificationResponse>, UnauthorizedHttpResult, ProblemHttpResult>> StartAsync(
        StartAgeVerificationRequest request,
        HttpContext context,
        AgeVerificationService verifications,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId
            || DuoraClaims.ReadSessionId(context.User) is not { } sessionId)
        {
            return TypedResults.Unauthorized();
        }

        return await verifications.RequestAsync(userId, sessionId, cancellationToken) switch
        {
            AgeVerificationRequestResult.Accepted accepted => TypedResults.Accepted(CurrentPath, ToResponse(accepted.Status)),
            AgeVerificationRequestResult.ProviderUnavailable => ApiProblems.Problem(
                StatusCodes.Status503ServiceUnavailable,
                ApiProblems.AgeVerificationUnavailable,
                "Verificação de idade indisponível no momento."),
            _ => ApiProblems.Problem(
                StatusCodes.Status409Conflict,
                ApiProblems.AgeVerificationNotAvailable,
                "Não há verificação de idade a iniciar para esta conta."),
        };
    }

    private static async Task<Results<Ok<AgeVerificationResponse>, UnauthorizedHttpResult>> GetCurrentAsync(
        HttpContext context,
        AgeVerificationService verifications,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        return TypedResults.Ok(ToResponse(await verifications.GetCurrentAsync(userId, cancellationToken)));
    }

    private static AgeVerificationResponse ToResponse(AgeVerificationStatus status) =>
        new(
            Enum.Parse<AgeVerificationStateCode>(status.State),
            Enum.Parse<AgeVerificationNextStepCode>(status.NextStep.ToString()),
            status.ContinueUrl?.AbsoluteUri);
}
