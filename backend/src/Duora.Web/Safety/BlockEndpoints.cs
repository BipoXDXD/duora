using Duora.Infrastructure.Safety;
using Duora.Web.Http;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Duora.Web.Safety;

/// <summary>Bloqueio de outra pessoa (API-S01, spec 8.4 e 7.3). Exige sessão pela política padrão.</summary>
internal static class BlockEndpoints
{
    private static readonly TimeSpan BusyRetryAfter = TimeSpan.FromSeconds(2);

    public static void MapBlockEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPut("/me/blocks/{targetUserId:guid}", BlockAsync)
            .WithTags("Safety")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// 204 para qualquer outra pessoa, exista ou não, já bloqueada ou não (G3, TM-24): a resposta não revela nada
    /// do alvo. Quem bloqueia vem só da sessão; o PUT não tem corpo, e um corpo é recusado (TM-04).
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult, UnauthorizedHttpResult>> BlockAsync(
        Guid targetUserId,
        HttpContext context,
        BlockAttemptLimiter limiter,
        BlockService blocks,
        CancellationToken cancellationToken)
    {
        if (DuoraClaims.ReadUserId(context.User) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        if (limiter.TryConsume(userId) is { } retryAfter)
        {
            return ApiProblems.TooManyRequests(context, retryAfter);
        }

        if (HasBody(context.Request))
        {
            return ApiProblems.Problem(
                StatusCodes.Status400BadRequest,
                ApiProblems.InvalidRequest,
                "Esta operação não aceita corpo.");
        }

        return await blocks.BlockAsync(userId, targetUserId, cancellationToken) switch
        {
            BlockOutcome.Done => TypedResults.NoContent(),
            BlockOutcome.SelfBlock => ApiProblems.Problem(
                StatusCodes.Status400BadRequest,
                ApiProblems.SelfBlock,
                "Não é possível bloquear a própria conta."),
            BlockOutcome.AccountNotEligible => ApiProblems.Problem(
                StatusCodes.Status403Forbidden,
                ApiProblems.AccountNotEligible,
                "A conta ainda não pode usar esta função."),
            BlockOutcome.UnknownAccount => TypedResults.Unauthorized(),
            BlockOutcome.Busy => Unavailable(context),
            var outcome => throw new InvalidOperationException($"Resultado de bloqueio sem resposta: {outcome}."),
        };
    }

    /// <summary>Guardas disputadas além das tentativas: nada gravado, o cliente tenta de novo (concurrency.md).</summary>
    private static ProblemHttpResult Unavailable(HttpContext context)
    {
        ApiProblems.SetRetryAfter(context, BusyRetryAfter);
        return ApiProblems.Problem(
            StatusCodes.Status503ServiceUnavailable,
            ApiProblems.TemporarilyUnavailable,
            "Não foi possível concluir agora. Tente de novo em instantes.");
    }

    private static bool HasBody(HttpRequest request) =>
        request.ContentLength is > 0 || (request.ContentLength is null && request.Headers.TransferEncoding.Count > 0);
}
