using Duora.Contracts.Safety;
using Duora.Infrastructure.Safety;
using Duora.Web.Accounts;
using Duora.Web.Http;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.Extensions.Options;

namespace Duora.Web.Safety;

/// <summary>
/// Prévia da reconciliação de inscrições legadas (API-ADM25, issue #321). Sessão administrativa do grupo
/// <c>/admin</c>, papel <c>moderator</c> lido ao vivo pela policy (antes de qualquer parâmetro) e TOTP recente. A
/// sessão é lida sem toque (ADR-0048) e o leitor revalida a moderação no próprio snapshot READ ONLY. O corte vem só
/// da configuração do servidor; o cliente escolhe apenas o cursor.
/// </summary>
internal static class RegistrationReconciliationEndpoints
{
    private const string CursorField = "cursor";

    /// <summary>Espera sugerida quando o snapshot passa do statement_timeout; a consulta é cara, não insistir já.</summary>
    private static readonly TimeSpan TimeoutRetryAfter = TimeSpan.FromSeconds(30);

    internal static void MapRegistrationReconciliationAdminEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGroup("/registration-reconciliations").WithTags("RegistrationReconciliation")
            .RequireAuthorization(AccountModerationPolicy.Name)
            .MapGet("/preview", PreviewAsync)
            .RequireRecentMfa()
            .WithReadOnlySession()
            .Produces<RegistrationReconciliationPreviewResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> PreviewAsync(
        string? cursorSubject,
        string? cursorMeasure,
        HttpContext http,
        LegacyRegistrationReconciliationPreviews previews,
        IOptions<RegistrationReconciliationCutoverOptions> cutover,
        ReconciliationPreviewLimiter limiter,
        CancellationToken cancellationToken)
    {
        var (userId, sessionId) = CurrentSession.Require(http);
        // Depois da autorização e antes do cursor: pedido inválido também conta, e o 429 não lê nada do banco.
        if (limiter.TryConsume(userId) is { } retryAfter)
        {
            return ApiProblems.TooManyRequests(http, retryAfter);
        }

        if (!TryReadCursor(cursorSubject, cursorMeasure, out var after))
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { [CursorField] = [RequestValidation.InvalidFormat] });
        }

        var evidence = cutover.Value.Evidence();
        var outcome = await previews.ReadAsModeratorAsync(
            new ModeratorSession(userId, sessionId), evidence?.Cutover, after, cancellationToken);
        switch (outcome)
        {
            case ReconciliationPreviewOutcome.Ready ready:
                return TypedResults.Ok(Response(ready.Preview, evidence?.Id));
            case ReconciliationPreviewOutcome.Forbidden:
                return ApiProblems.Problem(StatusCodes.Status403Forbidden, ApiProblems.AccountModerationForbidden);
            case ReconciliationPreviewOutcome.TimedOut:
                // Só esta rota: o statement_timeout da prévia não passa a valer como indisponibilidade em outras.
                ApiProblems.SetRetryAfter(http, TimeoutRetryAfter);
                return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable);
            default:
                throw new InvalidOperationException("Prévia sem resultado mapeado.");
        }
    }

    /// <summary>Os dois campos juntos ou nenhum; cada um um UUID no formato canônico com hífens, diferente de zero.</summary>
    private static bool TryReadCursor(string? subject, string? measure, out ReconciliationPreviewCursor? cursor)
    {
        cursor = null;
        if (subject is null && measure is null)
        {
            return true;
        }

        if (!TryReadId(subject, out var subjectId) || !TryReadId(measure, out var measureId))
        {
            return false;
        }

        cursor = new ReconciliationPreviewCursor(subjectId, measureId);
        return true;
    }

    private static bool TryReadId(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;

    private static RegistrationReconciliationPreviewResponse Response(ReconciliationPreview preview, string? evidenceId) => new(
        preview.AsOf.UtcDateTime,
        preview.SelectorVersion,
        evidenceId,
        [.. preview.RegistrationsByPrimaryIssue.OrderBy(entry => entry.Key)
            .Select(entry => new ReconciliationIssueCountResponse(entry.Key.ToString(), entry.Value))],
        preview.ActionableRegistrations,
        preview.HeldRegistrations,
        new ReconciliationUnitCountsResponse(preview.Units.Eligible, preview.Units.Blocked, preview.Units.Oversized),
        [.. preview.Page.Select(unit => new ReconciliationPreviewUnitResponse(
            unit.SubjectUserId,
            unit.OriginalMeasureId,
            unit.MeasureVersion,
            [.. unit.Items.Select(item => new ReconciliationPreviewItemResponse(
                item.EventId, item.RegistrationVersion, item.State.ToString(), item.MaxReleasedSeats, item.MaxPromotions))]))],
        preview.Next is { } next ? new ReconciliationPreviewCursorResponse(next.SubjectUserId, next.OriginalMeasureId) : null);
}
