using Duora.Core;
using Duora.Core.Identity;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Outbox;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Consumidor da outbox que abre a verificação no fornecedor (ADR-0024, decisão 1; TM-39). Toma um lote com lease,
/// chama o fornecedor fora de qualquer transação e com timeout, grava a referência e o fluxo com UPDATE condicional e
/// só então encerra a entrega, conferindo o token de fencing. Falha volta à fila pelo backoff; a conta continua
/// pendente o tempo todo, porque abrir a verificação não decide nada.
/// </summary>
public sealed partial class AgeVerificationDispatcher
{
    /// <summary>Nome das options de despacho (<c>AgeVerification:Dispatch</c>), separadas das do e-mail.</summary>
    internal const string DispatchOptionsName = "AgeVerification";

    /// <summary>Caminho da SPA para onde o fornecedor devolve o navegador.</summary>
    internal const string ReturnPath = "/conta/verificacao-etaria";

    private const string InvalidPayloadError = "InvalidPayload";
    private const string DisallowedFlowOriginError = "DisallowedFlowOrigin";
    private const string DuplicateReferenceError = "DuplicateReference";
    private const string ProviderMismatchError = "ProviderMismatch";
    private const string UniqueViolation = "23505";

    private const string LoadSql =
        """
        select state, request_nonce, policy_version, provider, provider_reference is not null
        from identity.age_verifications
        where id = @id
        """;

    private const string OpenedSql =
        """
        update identity.age_verifications
        set provider_reference = @reference, provider_flow_url = @flow_url, version = version + 1
        where id = @id and state = 'Pending' and provider_reference is null
        """;

    private readonly OutboxDeliveryQueue _queue;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IAgeEligibilityProvider _provider;
    private readonly AgeVerificationOptions _options;
    private readonly OutboxDispatchOptions _dispatch;
    private readonly PublicOrigin _publicOrigin;
    private readonly TimeProvider _time;
    private readonly ILogger<AgeVerificationDispatcher> _logger;

    internal AgeVerificationDispatcher(
        OutboxDeliveryQueue queue,
        NpgsqlDataSource dataSource,
        IAgeEligibilityProvider provider,
        AgeVerificationOptions options,
        OutboxDispatchOptions dispatch,
        PublicOrigin publicOrigin,
        TimeProvider time,
        ILogger<AgeVerificationDispatcher> logger)
    {
        _queue = queue;
        _dataSource = dataSource;
        _provider = provider;
        _options = options;
        _dispatch = dispatch;
        _publicOrigin = publicOrigin;
        _time = time;
        _logger = logger;
    }

    public bool IsEnabled => _options.IsEnabled;

    public TimeSpan PollInterval => _dispatch.PollInterval;

    public int BatchSize => _dispatch.BatchSize;

    /// <summary>Processa um lote e devolve quantas entregas tomou; zero quando a fila está vazia ou sem fornecedor.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsEnabled)
        {
            return 0;
        }

        var now = _time.GetUtcNow();
        var claimed = await _queue.ClaimAsync(
            AgeVerificationOutbox.Consumer,
            _dispatch.BatchSize,
            now,
            now + _dispatch.LeaseDuration,
            cancellationToken);

        // Um por vez: o lote limita a concorrência contra o fornecedor a uma chamada por worker.
        foreach (var delivery in claimed)
        {
            await OpenAsync(delivery, cancellationToken);
        }

        return claimed.Count;
    }

    private async Task OpenAsync(ClaimedDelivery delivery, CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() >= delivery.DeadlineAt)
        {
            LogExpired(_logger, delivery.Id);
            await SettleAsync(delivery, OutboxDeliveryStatus.Expired, error: null, cancellationToken);
            return;
        }

        if (!AgeVerificationOutbox.TryRead(delivery, out var message)
            || await LoadAsync(message.VerificationId, cancellationToken) is not { } verification)
        {
            LogInvalidPayload(_logger, delivery.Id);
            await SettleAsync(delivery, OutboxDeliveryStatus.Failed, InvalidPayloadError, cancellationToken);
            return;
        }

        if (!verification.NeedsProviderSession)
        {
            // Já decidida, expirada ou aberta por um worker que perdeu o lease: não há o que pedir ao fornecedor.
            await SettleAsync(delivery, OutboxDeliveryStatus.Delivered, error: null, cancellationToken);
            return;
        }

        if (verification.Provider != _provider.Name)
        {
            // Configuração trocada com solicitações abertas: outro fornecedor não conhece esta referência.
            LogProviderMismatch(_logger, delivery.Id);
            await SettleAsync(delivery, OutboxDeliveryStatus.Failed, ProviderMismatchError, cancellationToken);
            return;
        }

        AgeCheckSession session;
        try
        {
            session = await StartWithTimeoutAsync(message, verification, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Inclui o timeout e a resposta inválida do fornecedor. O parar do worker (token cancelado) propaga.
            await RetryOrFailAsync(delivery, exception, cancellationToken);
            return;
        }

        if (!_options.IsAllowedFlow(session.FlowUrl))
        {
            LogDisallowedFlow(_logger, delivery.Id);
            await SettleAsync(delivery, OutboxDeliveryStatus.Failed, DisallowedFlowOriginError, cancellationToken);
            return;
        }

        if (!await TryRecordSessionAsync(message.VerificationId, session, cancellationToken))
        {
            await SettleAsync(delivery, OutboxDeliveryStatus.Failed, DuplicateReferenceError, cancellationToken);
            return;
        }

        await SettleAsync(delivery, OutboxDeliveryStatus.Delivered, error: null, cancellationToken);
    }

    private async Task<StoredVerification?> LoadAsync(Guid verificationId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(LoadSql);
        command.Parameters.AddWithValue("id", verificationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredVerification(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetBoolean(4));
    }

    private async Task<AgeCheckSession> StartWithTimeoutAsync(
        AgeVerificationRequested message,
        StoredVerification verification,
        CancellationToken cancellationToken)
    {
        var request = new AgeCheckStart(
            message.VerificationId,
            verification.RequestNonce,
            _publicOrigin.Resolve(ReturnPath),
            verification.PolicyVersion);
        using var timeout = new CancellationTokenSource(_dispatch.SendTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        return await _provider.StartAsync(request, linked.Token);
    }

    /// <summary>Grava referência e fluxo só numa pendente ainda sem referência; falso se a referência já é de outra.</summary>
    private async Task<bool> TryRecordSessionAsync(Guid verificationId, AgeCheckSession session, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(OpenedSql);
        command.Parameters.AddWithValue("id", verificationId);
        command.Parameters.AddWithValue("reference", session.ProviderReference);
        command.Parameters.AddWithValue("flow_url", session.FlowUrl.AbsoluteUri);
        try
        {
            // Zero linhas: decidida ou aberta entre a leitura e aqui; nada a gravar, e a entrega termina.
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            LogDuplicateReference(_logger, verificationId);
            return false;
        }
    }

    private async Task RetryOrFailAsync(ClaimedDelivery delivery, Exception exception, CancellationToken cancellationToken)
    {
        // Só a categoria: a mensagem do fornecedor pode trazer dado da pessoa (spec 19.2).
        var cause = exception.GetType().Name;
        if (delivery.Attempts >= _dispatch.MaxAttempts)
        {
            LogGaveUp(_logger, delivery.Id, delivery.Attempts, cause);
            await SettleAsync(delivery, OutboxDeliveryStatus.Failed, cause, cancellationToken);
            return;
        }

        var now = _time.GetUtcNow();
        var retryAt = now + _dispatch.Backoff.DelayAfter(delivery.Attempts, Random.Shared.NextDouble());
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Abertura etária {DeliveryId} encerrada como {Status}")]
    private static partial void LogSettled(ILogger logger, Guid deliveryId, OutboxDeliveryStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Abertura etária {DeliveryId} passou do prazo e expira sem chamar o fornecedor")]
    private static partial void LogExpired(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Abertura etária {DeliveryId} tem payload inválido e vai para revisão")]
    private static partial void LogInvalidPayload(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fornecedor devolveu fluxo fora da allowlist na abertura {DeliveryId}; vai para revisão")]
    private static partial void LogDisallowedFlow(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Abertura etária {DeliveryId} é de outro fornecedor; vai para revisão")]
    private static partial void LogProviderMismatch(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fornecedor repetiu uma referência já usada na solicitação {VerificationId}; vai para revisão")]
    private static partial void LogDuplicateReference(ILogger logger, Guid verificationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Abertura etária {DeliveryId} falhou na tentativa {Attempt} ({Cause}); nova tentativa em {RetryAt}")]
    private static partial void LogWillRetry(ILogger logger, Guid deliveryId, int attempt, string cause, DateTimeOffset retryAt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Abertura etária {DeliveryId} falhou na tentativa {Attempt} ({Cause}); sem novas tentativas, vai para revisão")]
    private static partial void LogGaveUp(ILogger logger, Guid deliveryId, int attempt, string cause);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Abertura etária {DeliveryId} não foi encerrada por este worker: o lease venceu ou outro worker a assumiu")]
    private static partial void LogLeaseLost(ILogger logger, Guid deliveryId);

    private sealed record StoredVerification(
        string State,
        string RequestNonce,
        string PolicyVersion,
        string Provider,
        bool HasReference)
    {
        public bool NeedsProviderSession => State == nameof(AgeVerificationState.Pending) && !HasReference;
    }
}
