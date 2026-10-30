using System.Text.Json;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Duora.Infrastructure.Outbox;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.Extensions.Logging;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Pedido de suspensão gravado na transação da décima reprovação (issue #300). Payload só com ids. Esperar não gasta
/// tentativa; só falha conta para o teto, e o pedido morto fica na outbox para revisão, nunca some.
/// </summary>
internal static class SelfieAttemptsSuspensionOutbox
{
    public const string Consumer = "selfie-attempts-suspension";
    public const string EventType = "identity.selfie-attempts-exhausted";

    /// <summary>
    /// Longe de propósito: o pedido pode esperar outra medida por tempo indeterminado, e entrega esgotada depois do
    /// prazo viraria <c>Expired</c> em vez de <c>Failed</c>, saindo da revisão de entregas mortas.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromDays(3650);

    public static OutboxEvent Create(Guid userId, Guid verificationId, DateTimeOffset now)
    {
        var identity = new EventIdentity(EventType, 1, "selfie_verification", verificationId);
        var item = OutboxEvent.Record(identity, JsonSerializer.Serialize(new Payload(userId, verificationId), PayloadJson), now);
        item.AddDelivery(Consumer, verificationId.ToString("D"), now + Deadline);
        return item;
    }

    public static Payload? Read(string json) => JsonSerializer.Deserialize<Payload>(json, PayloadJson);

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    internal sealed record Payload(Guid UserId, Guid VerificationId);
}

/// <summary>
/// Consumidor do pedido de suspensão: chama <see cref="AccountSuspensions.SuspendAutomaticallyAsync"/> fora de
/// qualquer transação (ela abre a própria) e com a chave da tentativa que esgotou o limite, então reentregar não cria
/// segunda medida. Esperar não gasta tentativa: contenção de lock e outra medida ativa adiam a entrega. Falha de
/// verdade (exceção) reagenda com o erro e recuo exponencial; no teto, a entrega fica morta (<c>Failed</c>, para
/// revisão), com log de erro e métrica. Uma entrega que falha não trava as outras do lote.
/// </summary>
public sealed partial class SelfieAttemptsSuspensionDispatcher
{
    /// <summary>Texto fixo da auditoria (INV-10): o mesmo pedido, sempre, para a chave reconhecer a repetição.</summary>
    internal const string InternalReason =
        "Selfie reprovada 10 vezes: 5 no primeiro ciclo e 5 no segundo, depois da pausa de 1 hora (issue #300).";

    /// <summary>Falhas (exceções) aceitas por pedido antes de ele ficar morto.</summary>
    internal const int MaxFailures = 10;

    private const string Source = "selfie-verification";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstFailureDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxFailureDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinWaitForOtherMeasure = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxWaitForOtherMeasure = TimeSpan.FromHours(1);

    private readonly DuoraDbContext _db;
    private readonly OutboxDeliveryQueue _queue;
    private readonly AccountSuspensions _suspensions;
    private readonly SelfieSuspensionRequestMetrics _metrics;
    private readonly ILogger<SelfieAttemptsSuspensionDispatcher> _logger;
    private readonly TimeProvider _time;

    internal SelfieAttemptsSuspensionDispatcher(DuoraDbContext db, OutboxDeliveryQueue queue, TimeProvider time,
        SelfieSuspensionRequestMetrics metrics, ILogger<SelfieAttemptsSuspensionDispatcher> logger)
    {
        _db = db;
        _queue = queue;
        _suspensions = new AccountSuspensions(db, time);
        _metrics = metrics;
        _logger = logger;
        _time = time;
    }

    /// <returns>Quantas entregas foram tomadas neste lote.</returns>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var claims = await _queue.ClaimWithAttemptLimitAsync(SelfieAttemptsSuspensionOutbox.Consumer, 10, now, now + Lease,
            MaxFailures, cancellationToken);
        foreach (var dead in claims.Exhausted)
        {
            _metrics.RecordDead();
            LogDead(_logger, dead.Id, dead.Attempts, dead.Status.ToString());
        }

        foreach (var delivery in claims.Claimed)
        {
            await SuspendOrRetryAsync(delivery, cancellationToken);
        }

        return claims.Claimed.Count;
    }

    private async Task SuspendOrRetryAsync(ClaimedDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            await SuspendAsync(delivery, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Só o tipo: a mensagem do banco pode citar valores. A entrega volta à fila e gasta a tentativa do claim.
            _db.ChangeTracker.Clear();
            var now = _time.GetUtcNow();
            LogFailed(_logger, delivery.Id, delivery.Attempts, error.GetType().Name);
            await _queue.RescheduleAsync(delivery, now + FailureDelay(delivery.Attempts), error.GetType().Name, now,
                cancellationToken);
        }
    }

    private async Task SuspendAsync(ClaimedDelivery delivery, CancellationToken cancellationToken)
    {
        var payload = delivery.EventType == SelfieAttemptsSuspensionOutbox.EventType
            ? ReadPayload(delivery.Payload)
            : null;
        if (payload is null)
        {
            await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Failed, "InvalidPayload", _time.GetUtcNow(), cancellationToken);
            return;
        }

        // A contagem pode ter acabado enquanto o pedido esperava (aprovação, revogação de uma suspensão por selfie).
        var standing = await SelfieFailureLedger.StandingAsync(_db, payload.UserId, _time.GetUtcNow(), cancellationToken);
        if (standing is not SelfieAttemptStanding.Exhausted)
        {
            await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, "NoLongerExhausted", _time.GetUtcNow(), cancellationToken);
            return;
        }

        var outcome = await _suspensions.SuspendAutomaticallyAsync(
            SuspensionOrder.Automated(payload.UserId, SuspensionReason.SelfieVerificationFailed, InternalReason),
            ModerationCommandKey.ForSystem(Source, payload.VerificationId),
            cancellationToken);
        var now = _time.GetUtcNow();
        switch (outcome)
        {
            case SuspensionOutcome.Busy:
                await _queue.PostponeAsync(delivery, now + BusyDelay, "Busy", now, cancellationToken);
                break;
            case SuspensionOutcome.Suspended:
                await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, null, now, cancellationToken);
                break;
            case SuspensionOutcome.AlreadySuspended:
                await SettleOrWaitForOtherMeasureAsync(delivery, payload.UserId, now, cancellationToken);
                break;
            // Conta apagada, em exclusão ou privilegiada: não há o que suspender, e o limite da selfie continua valendo.
            case SuspensionOutcome.NotFound or SuspensionOutcome.NotSuspendable:
                await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, outcome.GetType().Name, now, cancellationToken);
                break;
            case SuspensionOutcome.KeyReused or SuspensionOutcome.Forbidden:
                _metrics.RecordDead();
                LogDead(_logger, delivery.Id, delivery.Attempts, outcome.GetType().Name);
                await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Failed, outcome.GetType().Name, now, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Resultado de suspensão desconhecido.");
        }
    }

    /// <summary>
    /// Outra medida ativa (por exemplo, por violação dos Termos) não substitui a suspensão por selfie: se o moderador a
    /// revogasse, a conta ficaria sem tentativas e sem suspensão por selfie para revogar. O pedido espera, sem gastar
    /// tentativa, com espera que cresce com a idade do pedido até 1 hora.
    /// </summary>
    private async Task SettleOrWaitForOtherMeasureAsync(ClaimedDelivery delivery, Guid userId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await _suspensions.FindOwnNoticeAsync(userId, cancellationToken) is { Reason: SuspensionReason.SelfieVerificationFailed })
        {
            await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, null, now, cancellationToken);
            return;
        }

        var wait = Clamp(now - delivery.OccurredAt, MinWaitForOtherMeasure, MaxWaitForOtherMeasure);
        await _queue.PostponeAsync(delivery, now + wait, "AwaitingOtherMeasure", now, cancellationToken);
    }

    private static TimeSpan FailureDelay(int attempts) =>
        Clamp(FirstFailureDelay * Math.Pow(2, Math.Max(0, attempts - 1)), FirstFailureDelay, MaxFailureDelay);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    private static SelfieAttemptsSuspensionOutbox.Payload? ReadPayload(string json)
    {
        try
        {
            return SelfieAttemptsSuspensionOutbox.Read(json) is { UserId: var user, VerificationId: var attempt } payload
                && user != Guid.Empty && attempt != Guid.Empty
                ? payload
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Selfie suspension request {DeliveryId} failed on attempt {Attempts} ({ErrorType}); retrying.")]
    private static partial void LogFailed(ILogger logger, Guid deliveryId, int attempts, string errorType);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Selfie suspension request {DeliveryId} is dead after {Attempts} attempts ({Reason}); review the outbox.")]
    private static partial void LogDead(ILogger logger, Guid deliveryId, int attempts, string reason);
}
