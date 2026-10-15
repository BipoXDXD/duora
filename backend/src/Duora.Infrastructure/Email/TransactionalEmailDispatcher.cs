using System.Diagnostics.CodeAnalysis;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Outbox;
using Microsoft.Extensions.Logging;

namespace Duora.Infrastructure.Email;

/// <summary>
/// Consumidor da outbox de e-mails de conta (spec 10.6): toma um lote com lease, envia fora da transação e
/// conclui só se ainda detiver o token de fencing. A entrega é at-least-once; a chave de idempotência passada ao
/// envio (o id da entrega) é o que evita a segunda cópia quando um worker que perdeu o lease já tinha enviado.
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
        var claimed = await _queue.ClaimAsync(
            AccountEmailOutbox.Consumer,
            _options.BatchSize,
            now,
            now + _options.LeaseDuration,
            cancellationToken);

        foreach (var delivery in claimed)
        {
            await DeliverAsync(delivery, cancellationToken);
        }

        return claimed.Count;
    }

    private async Task DeliverAsync(ClaimedDelivery delivery, CancellationToken cancellationToken)
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

    private bool TryCompose(ClaimedDelivery delivery, [NotNullWhen(true)] out TransactionalEmail? email)
    {
        email = null;
        if (!AccountEmailOutbox.TryRead(delivery, out var message) || !EmailAddress.TryParse(message.To, out var to))
        {
            return false;
        }

        email = (message.Kind, message.Token) switch
        {
            (AccountEmailKind.EmailConfirmation, { Length: > 0 } token) => _emails.EmailConfirmation(to, message.UserId, token),
            (AccountEmailKind.PasswordReset, { Length: > 0 } token) => _emails.PasswordReset(to, message.UserId, token),
            (AccountEmailKind.RegistrationAttemptOnExistingAccount, _) => _emails.RegistrationAttemptOnExistingAccount(to),
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
        Level = LogLevel.Warning,
        Message = "Entrega {DeliveryId} não foi encerrada por este worker: o lease venceu ou outro worker a assumiu")]
    private static partial void LogLeaseLost(ILogger logger, Guid deliveryId);
}
