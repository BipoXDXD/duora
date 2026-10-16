using Duora.Core.Jobs;
using Duora.Core.Media;
using Duora.Infrastructure.Outbox;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Duora.Infrastructure.Media;

/// <summary>Parâmetros do consumidor <c>media-processing</c> (seção <c>Media:Processing</c>, ADR-0025).</summary>
internal sealed class MediaProcessingOptions
{
    public const string Section = "Media:Processing";

    /// <summary>Precisa cobrir a leitura do original, a sanitização e a gravação dos derivados.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int MaxAttempts { get; set; } = 8;

    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    public ExponentialBackoff Backoff => new(InitialRetryDelay, MaxRetryDelay);

    public bool IsConsistent() =>
        LeaseDuration > TimeSpan.Zero
        && PollInterval > TimeSpan.Zero
        && MaxAttempts >= 1
        && InitialRetryDelay > TimeSpan.Zero
        && MaxRetryDelay >= InitialRetryDelay;
}

/// <summary>
/// Consumidor da outbox de mídia (ADR-0025): um upload por vez (concorrência 1, spec 4.3). Toma a entrega com lease e
/// fencing, leva o upload a <c>Processing</c>, sanitiza fora de qualquer transação, grava os derivados em chaves fixas
/// do upload e conclui com <c>UPDATE</c> condicional pela versão. Tudo é repetível: outro worker que retome depois de
/// um lease vencido sobrescreve os mesmos derivados, e um upload já decidido só encerra a entrega.
/// </summary>
public sealed partial class MediaProcessingDispatcher
{
    private const string InvalidPayloadError = "InvalidPayload";
    private const string OriginalMissingError = "OriginalMissing";

    private readonly OutboxDeliveryQueue _queue;
    private readonly DuoraDbContext _db;
    private readonly IObjectStore _store;
    private readonly IPhotoSanitizer _sanitizer;
    private readonly TimeProvider _time;
    private readonly MediaOptions _media;
    private readonly MediaProcessingOptions _options;
    private readonly ILogger<MediaProcessingDispatcher> _logger;

    internal MediaProcessingDispatcher(
        OutboxDeliveryQueue queue,
        DuoraDbContext db,
        IObjectStore store,
        IPhotoSanitizer sanitizer,
        TimeProvider time,
        MediaOptions media,
        MediaProcessingOptions options,
        ILogger<MediaProcessingDispatcher> logger)
    {
        _queue = queue;
        _db = db;
        _store = store;
        _sanitizer = sanitizer;
        _time = time;
        _media = media;
        _options = options;
        _logger = logger;
    }

    private enum Settlement
    {
        Delivered,
        Failed,
        LeaseLost,
    }

    public bool Enabled => _media.UploadsEnabled;

    public TimeSpan PollInterval => _options.PollInterval;

    /// <summary>Processa no máximo um upload e devolve quantas entregas tomou (0 ou 1).</summary>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var claimed = await _queue.ClaimAsync(
            MediaProcessingOutbox.Consumer,
            batchSize: 1,
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
            await SettleAsync(delivery, OutboxDeliveryStatus.Expired, error: null, cancellationToken);
            return;
        }

        if (!MediaProcessingOutbox.TryRead(delivery, out var message))
        {
            LogInvalidPayload(_logger, delivery.Id);
            await SettleAsync(delivery, OutboxDeliveryStatus.Failed, InvalidPayloadError, cancellationToken);
            return;
        }

        Settlement settlement;
        try
        {
            settlement = await ProcessAsync(message.UploadId, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Storage ou banco indisponível. Parar o worker (token cancelado) propaga: o lease vence e outro retoma.
            await RetryOrFailAsync(delivery, exception, cancellationToken);
            return;
        }

        switch (settlement)
        {
            case Settlement.Delivered:
                await SettleAsync(delivery, OutboxDeliveryStatus.Delivered, error: null, cancellationToken);
                break;
            case Settlement.Failed:
                await SettleAsync(delivery, OutboxDeliveryStatus.Failed, OriginalMissingError, cancellationToken);
                break;
            case Settlement.LeaseLost:
                LogLeaseLost(_logger, delivery.Id);
                break;
        }
    }

    private async Task<Settlement> ProcessAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await _db.Set<Upload>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == uploadId, cancellationToken);
        if (upload is null || upload.State is not (UploadState.Quarantined or UploadState.Processing))
        {
            // Já decidido (ou excluído) por uma tentativa anterior: só falta encerrar a entrega.
            return Settlement.Delivered;
        }

        var version = upload.Version + 1;
        if (!await MoveAsync(upload.Id, upload.Version, UploadState.Processing, rejection: null, cancellationToken))
        {
            return Settlement.LeaseLost;
        }

        var original = await ReadOriginalAsync(upload, cancellationToken);
        if (original is null)
        {
            LogOriginalMissing(_logger, upload.Id);
            return Settlement.Failed;
        }

        var fingerprint = upload.Fingerprint()
            ?? throw new InvalidOperationException("Upload em Processing sem impressão digital.");
        var outcome = await _sanitizer.SanitizeAsync(fingerprint.Format, original, cancellationToken);
        return outcome switch
        {
            SanitizeOutcome.Rejected rejected => await RejectAsync(upload, version, rejected.Reason, cancellationToken),
            SanitizeOutcome.Sanitized sanitized => await PublishAsync(upload, fingerprint, version, sanitized, cancellationToken),
            _ => throw new InvalidOperationException("Sanitização sem desfecho."),
        };
    }

    private async Task<byte[]?> ReadOriginalAsync(Upload upload, CancellationToken cancellationToken)
    {
        await using var stream = await _store.OpenReadAsync(ObjectKey.Parse(upload.QuarantineKey), cancellationToken);
        if (stream is null)
        {
            return null;
        }

        // O original tem no máximo 5 MiB (CHECK de size_bytes); lê só até o tamanho gravado, e um byte a mais é erro.
        await using var exact = new ExactLengthStream(stream, upload.SizeBytes);
        using var copy = new MemoryStream((int)upload.SizeBytes);
        await exact.CopyToAsync(copy, cancellationToken);
        return copy.ToArray();
    }

    private async Task<Settlement> RejectAsync(Upload upload, long version, PhotoRejection reason, CancellationToken cancellationToken)
    {
        var code = PhotoRejections.Code(reason);
        if (!await MoveAsync(upload.Id, version, UploadState.Rejected, code, cancellationToken))
        {
            return Settlement.LeaseLost;
        }

        LogRejected(_logger, upload.Id, code);
        await DeleteQuietlyAsync(ObjectKey.Parse(upload.QuarantineKey), upload.Id);
        return Settlement.Delivered;
    }

    /// <summary>
    /// Derivados primeiro, depois o commit que os referencia: se o commit falhar, eles ficam órfãos e a próxima
    /// tentativa os sobrescreve nas mesmas chaves (spec 16.2). Nunca se publica por ter gravado no storage.
    /// </summary>
    private async Task<Settlement> PublishAsync(
        Upload upload,
        UploadFingerprint fingerprint,
        long version,
        SanitizeOutcome.Sanitized sanitized,
        CancellationToken cancellationToken)
    {
        var verdict = MediaReview.VerdictAfterSanitizing(
            _media.Review ?? throw new InvalidOperationException("Processamento de mídia sem política de revisão."));
        foreach (var variant in sanitized.Variants)
        {
            await using var content = new MemoryStream(variant.Content, writable: false);
            await _store.PutAsync(
                ObjectKey.ForDerivative(upload.Id, variant.Kind),
                content,
                variant.Content.Length,
                IPhotoSanitizer.VariantContentType,
                cancellationToken);
        }

        var now = _time.GetUtcNow();
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (!await MoveAsync(upload.Id, version, verdict, rejection: null, cancellationToken))
        {
            return Settlement.LeaseLost;
        }

        var asset = MediaAsset.Approve(
            upload.Id,
            upload.OwnerId,
            sanitized.Detected,
            sanitized.Width,
            sanitized.Height,
            fingerprint.Sha256.Span,
            now);
        _db.Add(asset);
        foreach (var variant in sanitized.Variants)
        {
            _db.Add(MediaVariant.Create(
                asset.Id,
                variant.Kind,
                ObjectKey.ForDerivative(upload.Id, variant.Kind),
                variant.Width,
                variant.Height,
                variant.Content.Length,
                now));
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogApproved(_logger, upload.Id, asset.Id);

        // O original só existe até aqui (spec 12.4): o asset nasce sem original_key.
        await DeleteQuietlyAsync(ObjectKey.Parse(upload.QuarantineKey), upload.Id);
        return Settlement.Delivered;
    }

    /// <summary>Transição condicional pela versão (spec 4.2); falso quando outra tentativa já mexeu no upload.</summary>
    private async Task<bool> MoveAsync(Guid uploadId, long version, UploadState to, string? rejection, CancellationToken cancellationToken)
    {
        var from = Enum.GetValues<UploadState>().Where(state => UploadLifecycle.CanMove(state, to)).ToList();
        var moved = await _db.Set<Upload>()
            .Where(u => u.Id == uploadId && u.Version == version && from.Contains(u.State))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(u => u.State, to)
                    .SetProperty(u => u.RejectionCode, rejection)
                    .SetProperty(u => u.Version, u => u.Version + 1),
                cancellationToken);
        return moved == 1;
    }

    private async Task RetryOrFailAsync(ClaimedDelivery delivery, Exception exception, CancellationToken cancellationToken)
    {
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
        OutboxDeliveryStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        if (!await _queue.SettleAsync(delivery, status, error, _time.GetUtcNow(), cancellationToken))
        {
            LogLeaseLost(_logger, delivery.Id);
        }
    }

    /// <summary>Melhor esforço: original que não sair fica para a limpeza de órfãos, com log (spec 16.2).</summary>
    private async Task DeleteQuietlyAsync(ObjectKey key, Guid uploadId)
    {
        try
        {
            await _store.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogOrphanLeft(_logger, uploadId, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload {UploadId} aprovado como asset {AssetId}")]
    private static partial void LogApproved(ILogger logger, Guid uploadId, Guid assetId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload {UploadId} rejeitado: {RejectionCode}")]
    private static partial void LogRejected(ILogger logger, Guid uploadId, string rejectionCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Entrega {DeliveryId} de mídia tem payload inválido e vai para revisão")]
    private static partial void LogInvalidPayload(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Original do upload {UploadId} não está na quarentena; entrega vai para revisão")]
    private static partial void LogOriginalMissing(ILogger logger, Guid uploadId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Processamento da entrega {DeliveryId} falhou na tentativa {Attempt} ({Cause}); nova tentativa em {RetryAt}")]
    private static partial void LogWillRetry(ILogger logger, Guid deliveryId, int attempt, string cause, DateTimeOffset retryAt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Processamento da entrega {DeliveryId} falhou na tentativa {Attempt} ({Cause}); vai para revisão")]
    private static partial void LogGaveUp(ILogger logger, Guid deliveryId, int attempt, string cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entrega {DeliveryId} de mídia não foi encerrada por este worker: lease vencido ou upload já tomado")]
    private static partial void LogLeaseLost(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Objeto do upload {UploadId} não foi apagado ({Cause}); fica para a limpeza de órfãos")]
    private static partial void LogOrphanLeft(ILogger logger, Guid uploadId, string cause);
}
