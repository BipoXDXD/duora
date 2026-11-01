using Duora.Contracts.Safety;
using Duora.Core.Media;
using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Duora.Web.Http;

namespace Duora.Web.Safety;

internal static partial class AccountSuspensionEndpoints
{
    private static async Task<IResult> RecoverSelfieAsync(
        Guid userId,
        Guid deliveryId,
        RecoverSelfieSuspensionRequest request,
        HttpContext http,
        AccountSuspensions suspensions,
        CancellationToken cancellationToken)
    {
        if (!IdempotencyKey.TryParse(http.Request.Headers["Idempotency-Key"].ToString(), out var key))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_INVALID");
        }

        if (deliveryId == Guid.Empty)
        {
            return Invalid("deliveryId");
        }

        var moderator = Moderator(http);
        if (request.InternalReason is not { } reason
            || Parse(() => SuspensionOrder.ByModerator(moderator.UserId, userId,
                SuspensionReason.SelfieVerificationFailed, reason)) is not { } order)
        {
            return Invalid("internalReason");
        }

        var outcome = await suspensions.SuspendAsModeratorAsync(moderator, order,
            ModerationCommandKey.ForModerator(moderator.UserId, key.Value), cancellationToken, failedDeliveryId: deliveryId);
        return outcome switch
        {
            SuspensionOutcome.Suspended applied => Created(http, applied.View),
            SuspensionOutcome.RecoveryUnavailable => ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.SelfieRecoveryUnavailable),
            SuspensionOutcome.RecoveryNotRequired => ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.SelfieRecoveryNotRequired),
            SuspensionOutcome.AlreadySuspended => ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.AccountAlreadySuspended),
            SuspensionOutcome.KeyReused => ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.IdempotencyKeyReused),
            SuspensionOutcome.NotSuspendable => ApiProblems.Problem(StatusCodes.Status409Conflict, ApiProblems.AccountNotSuspendable),
            SuspensionOutcome.NotFound => NotFound(),
            SuspensionOutcome.Forbidden => Forbidden(),
            SuspensionOutcome.Busy => Busy(http),
            _ => throw new InvalidOperationException("Recuperação de suspensão sem resultado mapeado."),
        };
    }
}
