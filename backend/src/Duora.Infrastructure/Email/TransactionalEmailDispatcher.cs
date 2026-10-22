using System.Diagnostics.CodeAnalysis;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Outbox;
using Microsoft.Extensions.Logging;

namespace Duora.Infrastructure.Email;

/// <summary>
/// Consumidor da outbox de e-mails de conta (spec 10.6): toma um lote com lease, envia fora da transação e
/// conclui só se ainda detiver o token de fencing. A entrega é at-least-once; o adaptador recebe o id da entrega
/// para deduplicação quando suportada. No SMTP, aceite remoto seguido de falha local pode gerar outra cópia.
/// </summary>
public sealed partial class TransactionalEmailDispatcher
{
    private const string InvalidPayloadError = "InvalidPayload";

    private readonly OutboxDeliveryQueue _queue;
    private readonly ITransactionalEmailSender _sender;
    private readonly AccountEmails _emails;
    private readonly TimeProvider _time;
    private readonly OutboxDispatchOptions _options;
    private readonly ILogger<TransactionalEmailDispatcher> _logger;

    internal TransactionalEmailDispatcher(
        OutboxDeliveryQueue queue,
        ITransactionalEmailSender sender,
        AccountEmails emails,
        TimeProvider time,
        OutboxDispatchOptions options,
        ILogger<TransactionalEmailDispatcher> logger)
    {
        _queue = queue;
        _sender = sender;
        _emails = emails;
        _time = time;
        _options = options;
        _logger = logger;
    }

    public TimeSpan PollInterval => _options.PollInterval;

    public int BatchSize => _options.BatchSize;

    /// <summary>Processa um lote e devolve quantas entregas tomou; zero quando a fila está vazia.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var result = await _queue.ClaimWithAttemptLimitAsync(
            AccountEmailOutbox.Consumer,
            _options.BatchSize,
            now,
            now + _options.LeaseDuration,
            _options.MaxAttempts,
            cancellationToken);

        foreach (var exhausted in result.Exhausted)
        {
            LogExhausted(exhausted);
        }

        var waiting = new Queue<ClaimedDelivery>(result.Claimed);
        while (waiting.TryDequeue(out var delivery))
        {
            await DeliverAsync(delivery, waiting, cancellationToken);
        }

        return result.Claimed.Count;
    }

    private async Task DeliverAsync(
        ClaimedDelivery delivery,
        IReadOnlyCollection<ClaimedDelivery> waiting,
        CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() >= delivery.DeadlineAt)
        {
            LogExpired(_logger, delivery.Id);
            await SettleAsync(delivery, OutboxDeliveryStatus.Expired, error: null, cancellationToken);
            return;
        }

        if (!TryCompose(delivery, out var email))
        {
            LogInvalidPayload(_logger, delivery.Id);
            await SettleAsync(delivery, OutboxDeliveryStatus.Failed, InvalidPayloadError, cancellationToken);
            return;
        }

        // Heartbeat: renova o lease desta entrega e das que ainda esperam no lote, para um lote lento não perder
        // as últimas para outro worker. Se esta já não foi renovada (pausa longa, outro worker a assumiu), não
        // envia; o próximo claim a recupera.
        var renewed = await _queue.RenewLeasesAsync([delivery, .. waiting], _options.LeaseDuration, _time, cancellationToken);
        if (!renewed.Contains(delivery.Id))
        {
            LogSkippedWithoutOwnership(_logger, delivery.Id);
            return;
        }

        try
        {
            await SendWithTimeoutAsync(email, delivery, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Inclui o timeout do envio. O parar do worker (token cancelado) propaga: a entrega fica em voo e
            // outro worker a retoma quando o lease vencer.
            await RetryOrFailAsync(delivery, exception, cancellationToken);
            return;
        }

        await SettleAsync(delivery, OutboxDeliveryStatus.Delivered, error: null, cancellationToken);
    }

    private void LogExhausted(ExhaustedDelivery exhausted)
    {
        // Passado o deadline, o e-mail já não serve (link vencido): expira como no caminho normal, sem revisão.
        if (exhausted.Status == OutboxDeliveryStatus.Expired)
        {
            LogExpired(_logger, exhausted.Id);
            return;
        }

        LogExhaustedForReview(_logger, exhausted.Id, AccountEmailOutbox.Consumer, exhausted.Attempts);
    }

    private bool TryCompose(ClaimedDelivery delivery, [NotNullWhen(true)] out TransactionalEmail? email)
    {
        email = null;
        if (!AccountEmailOutbox.TryRead(delivery, out var message) || !EmailAddress.TryParse(message.To, out var to))
        {
            return false;
        }

        if (message.PreferredTimeZone is { } zone
            && (!TimeZones.IsSupported(zone) || AccountEmailOutbox.SecurityChangeOf(message.Kind) is null))
        {
            return false;
        }
        var language = EmailLanguageTag.ParseOrDefault(message.Language);
        email = (message.Kind, message.Token) switch
        {
            (AccountEmailKind.EmailConfirmation, { Length: > 0 } token) => _emails.EmailConfirmation(to, message.UserId, token, language),
            (AccountEmailKind.PasswordReset, { Length: > 0 } token) => _emails.PasswordReset(to, message.UserId, token, language),
            (AccountEmailKind.RegistrationAttemptOnExistingAccount, _) => _emails.RegistrationAttemptOnExistingAccount(to, language),
            (AccountEmailKind.PendingRegistrationReminder, _) => _emails.PendingRegistrationReminder(to, language),
            (AccountEmailKind.PasswordResetForUnknownAddress, _) => _emails.PasswordResetForUnknownAddress(to, language),
            // Sem link por regra: com token, o payload é defeito de quem gravou e não sai.
            (AccountEmailKind.PasswordResetUnavailable, null) => _emails.PasswordResetUnavailable(to, language),
            // Aviso nunca leva credencial: com token, o payload é defeito de quem gravou e não sai. O aviso é
            // bilíngue, então o idioma do pedido não conta.
            (_, null) when AccountEmailOutbox.SecurityChangeOf(message.Kind) is { } change =>
                _emails.SecurityNotice(to, change, delivery.OccurredAt, message.PreferredTimeZone),
            _ => null,
        };
        return email is not null;
    }

    private async Task SendWithTimeoutAsync(
        TransactionalEmail email,
        ClaimedDelivery delivery,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.SendTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await _sender.SendAsync(email, delivery.Id.ToString("N"), linked.Token);
    }

    private async Task RetryOrFailAsync(ClaimedDelivery delivery, Exception exception, CancellationToken cancellationToken)
    {
        // Só a categoria: a mensagem de um provedor pode trazer o destinatário ou o corpo (spec 19.2).
        var cause = exception.GetType().Name;
        if (delivery.Attempts >= _options.MaxAttempts)
        {
            LogGaveUp(_logger, delivery.Id, delivery.Attempts, cause);
            await SettleAsync(delivery, OutboxDeliveryStatus.Failed, cause, cancellationToken);
            return;
        }

        var now = _time.GetUtcNow();
        var retryAt = now + _options.Backoff.DelayAfter(delivery.Attempts, Random.Shared.NextDouble());
        LogWillRetry(_logger, delivery.Id, delivery.Attempts, cause, retryAt);
        if (!await _queue.RescheduleAsync(delivery, retryAt, cause, now, cancellationToken))
        {
            LogLeaseLost(_logger, delivery.Id);
        }
    }

    private async Task SettleAsync(
        ClaimedDelivery delivery,
        OutboxDeliveryStatus finalStatus,
        string? error,
        CancellationToken cancellationToken)
    {
        if (await _queue.SettleAsync(delivery, finalStatus, error, _time.GetUtcNow(), cancellationToken))
        {
            LogSettled(_logger, delivery.Id, finalStatus);
            return;
        }

        LogLeaseLost(_logger, delivery.Id);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Entrega {DeliveryId} encerrada como {Status}")]
    private static partial void LogSettled(ILogger logger, Guid deliveryId, OutboxDeliveryStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Entrega {DeliveryId} passou do prazo e expira sem envio")]
    private static partial void LogExpired(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Entrega {DeliveryId} tem payload inválido e vai para revisão")]
    private static partial void LogInvalidPayload(ILogger logger, Guid deliveryId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Envio da entrega {DeliveryId} falhou na tentativa {Attempt} ({Cause}); nova tentativa em {RetryAt}")]
    private static partial void LogWillRetry(ILogger logger, Guid deliveryId, int attempt, string cause, DateTimeOffset retryAt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Envio da entrega {DeliveryId} falhou na tentativa {Attempt} ({Cause}); sem novas tentativas, vai para revisão")]
    private static partial void LogGaveUp(ILogger logger, Guid deliveryId, int attempt, string cause);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Entrega {DeliveryId} do consumidor {Consumer} esgotou as tentativas após {Attempts} tentativa(s) sem resultado gravado; vai para revisão")]
    private static partial void LogExhaustedForReview(ILogger logger, Guid deliveryId, string consumer, int attempts);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Entrega {DeliveryId} não foi enviada: o heartbeat não renovou a posse (lease vencido ou outro worker a assumiu)")]
    private static partial void LogSkippedWithoutOwnership(ILogger logger, Guid deliveryId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Entrega {DeliveryId} não foi encerrada por este worker: o lease venceu ou outro worker a assumiu")]
    private static partial void LogLeaseLost(ILogger logger, Guid deliveryId);
}
