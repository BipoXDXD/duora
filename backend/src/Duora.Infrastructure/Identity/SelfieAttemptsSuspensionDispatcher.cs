using System.Text.Json;
using Duora.Core.Safety;
using Duora.Infrastructure.Outbox;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Pedido de suspensão gravado na transação da décima reprovação (issue #300). Payload só com ids; a entrega não tem
/// teto de tentativas, porque perder o pedido deixaria a conta sem o bloqueio que o dono decidiu.
/// </summary>
internal static class SelfieAttemptsSuspensionOutbox
{
    public const string Consumer = "selfie-attempts-suspension";
    public const string EventType = "identity.selfie-attempts-exhausted";

    /// <summary>Só informativo: o claim sem teto não encerra a entrega pelo prazo.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromDays(30);

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
/// segunda medida. Contenção de lock reagenda; o lease vencido de um worker que caiu devolve a entrega à fila.
/// </summary>
public sealed class SelfieAttemptsSuspensionDispatcher
{
    /// <summary>Texto fixo da auditoria (INV-10): o mesmo pedido, sempre, para a chave reconhecer a repetição.</summary>
    internal const string InternalReason =
        "Selfie reprovada 10 vezes: 5 no primeiro ciclo e 5 no segundo, depois da pausa de 1 hora (issue #300).";

    private const string Source = "selfie-verification";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly OutboxDeliveryQueue _queue;
    private readonly AccountSuspensions _suspensions;
    private readonly TimeProvider _time;

    internal SelfieAttemptsSuspensionDispatcher(DuoraDbContext db, OutboxDeliveryQueue queue, TimeProvider time)
    {
        _queue = queue;
        _suspensions = new AccountSuspensions(db, time);
        _time = time;
    }

    /// <returns>Quantas entregas foram tomadas neste lote.</returns>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var claimed = await _queue.ClaimAsync(SelfieAttemptsSuspensionOutbox.Consumer, 10, now, now + Lease, cancellationToken);
        foreach (var delivery in claimed)
        {
            await SuspendAsync(delivery, cancellationToken);
        }

        return claimed.Count;
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

        var outcome = await _suspensions.SuspendAutomaticallyAsync(
            SuspensionOrder.Automated(payload.UserId, SuspensionReason.SelfieVerificationFailed, InternalReason),
            ModerationCommandKey.ForSystem(Source, payload.VerificationId),
            cancellationToken);
        var now = _time.GetUtcNow();
        switch (outcome)
        {
            case SuspensionOutcome.Busy:
                await _queue.RescheduleAsync(delivery, now + RetryDelay, "Busy", now, cancellationToken);
                break;
            case SuspensionOutcome.Suspended or SuspensionOutcome.AlreadySuspended:
                await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, null, now, cancellationToken);
                break;
            // Conta apagada, em exclusão ou privilegiada: não há o que suspender, e o limite da selfie continua valendo.
            case SuspensionOutcome.NotFound or SuspensionOutcome.NotSuspendable:
                await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, outcome.GetType().Name, now, cancellationToken);
                break;
            case SuspensionOutcome.KeyReused or SuspensionOutcome.Forbidden:
                await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Failed, outcome.GetType().Name, now, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Resultado de suspensão desconhecido.");
        }
    }

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
}
