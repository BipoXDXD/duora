using System.Security.Cryptography;
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
    private const string OriginalMismatchError = "OriginalMismatch";
    private const string AttemptsExhaustedError = "AttemptsExhausted";

    /// <summary>
    /// Tentativas além de <see cref="MediaProcessingOptions.MaxAttempts"/> que só encerram o upload, sem decodificar
    /// (o processo caiu nas anteriores). Se nem elas terminarem, o teto do claim encerra a entrega (poison message).
    /// </summary>
    internal const int GiveUpAttempts = 2;

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
        OriginalMissing,
        OriginalMismatch,
        LeaseLost,
    }

    public bool Enabled => _media.UploadsEnabled;

    public TimeSpan PollInterval => _options.PollInterval;

    /// <summary>
    /// Processa no máximo um upload e devolve quantas entregas tomou ou encerrou no claim (0 ou 1). Sem heartbeat: o
    /// lote é de um, o lease cobre o envelope completo e margem de storage/commit (conferido no boot) e o efeito no storage é
    /// protegido pela versão do upload, não pelo lease.
    /// </summary>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var result = await _queue.ClaimWithAttemptLimitAsync(
            MediaProcessingOutbox.Consumer,
            batchSize: 1,
            now,
            now + _options.LeaseDuration,
            _options.MaxAttempts + GiveUpAttempts,
            cancellationToken);
        foreach (var exhausted in result.Exhausted)
        {
            await AbandonExhaustedAsync(exhausted, cancellationToken);
        }

        foreach (var delivery in result.Claimed)
        {
            await DeliverAsync(delivery, cancellationToken);
        }

        return result.Claimed.Count + result.Exhausted.Count;
    }

    /// <summary>
    /// A entrega já está encerrada pelo claim; falta o upload, que ficaria em <c>Processing</c> ou <c>Quarantined</c>
    /// ocupando vaga com o original na quarentena. Falha aqui fica no log: a entrega não volta.
    /// </summary>
    private async Task AbandonExhaustedAsync(ExhaustedDelivery exhausted, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(exhausted.RecipientKey, "D", out var uploadId))
        {
            LogInvalidPayload(_logger, exhausted.Id);
            return;
        }

        LogExhaustedAtClaim(_logger, exhausted.Id, exhausted.Attempts, uploadId);
        if (!await AbandonAfterLastAttemptAsync(uploadId, cancellationToken))
        {
            LogLeaseLost(_logger, exhausted.Id);
        }
    }

    private async Task DeliverAsync(ClaimedDelivery delivery, CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() >= delivery.DeadlineAt)
        {
            await ExpireAsync(delivery, cancellationToken);
            return;
        }

        if (!MediaProcessingOutbox.TryRead(delivery, out var message))
        {
            LogInvalidPayload(_logger, delivery.Id);
            await SettleAsync(delivery, OutboxDeliveryStatus.Failed, InvalidPayloadError, cancellationToken);
            return;
        }

        if (delivery.Attempts > _options.MaxAttempts)
        {
            // Nenhuma tentativa anterior chegou ao catch: o processo caiu no meio (uma imagem que derruba o decoder,
            // por exemplo) e o lease venceu. Decodificar de novo seria laço de crash; encerra o upload sem tocar nele.
            await GiveUpAsync(delivery, message.UploadId, AttemptsExhaustedError, cancellationToken);
            return;
        }

        Settlement settlement;
        try
        {
            settlement = await ProcessAsync(message.UploadId, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Storage ou banco indisponível, ou defeito do decoder. Parar o worker (token cancelado) propaga: o lease
            // vence e outro retoma.
            await RetryOrAbandonAsync(delivery, message.UploadId, exception, cancellationToken);
            return;
        }

        switch (settlement)
        {
            case Settlement.Delivered:
                await SettleAsync(delivery, OutboxDeliveryStatus.Delivered, error: null, cancellationToken);
                break;
            case Settlement.OriginalMissing:
                await SettleAsync(delivery, OutboxDeliveryStatus.Failed, OriginalMissingError, cancellationToken);
                break;
            case Settlement.OriginalMismatch:
                await SettleAsync(delivery, OutboxDeliveryStatus.Failed, OriginalMismatchError, cancellationToken);
                break;
            case Settlement.LeaseLost:
                LogLeaseLost(_logger, delivery.Id);
                break;
        }
    }

    /// <summary>
    /// Prazo da entrega vencido (o mesmo do abandono do upload): processar já não faz sentido, mas o upload não pode
    /// ficar em <c>Quarantined</c> ou <c>Processing</c> ocupando vaga de pendente com o original na quarentena.
    /// </summary>
    private async Task ExpireAsync(ClaimedDelivery delivery, CancellationToken cancellationToken)
    {
        if (MediaProcessingOutbox.TryRead(delivery, out var message))
        {
            LogExpired(_logger, delivery.Id, message.UploadId);
            if (!await AbandonAfterLastAttemptAsync(message.UploadId, cancellationToken))
            {
                LogLeaseLost(_logger, delivery.Id);
                return;
            }
        }

        await SettleAsync(delivery, OutboxDeliveryStatus.Expired, error: null, cancellationToken);
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

        var fingerprint = upload.Fingerprint()
            ?? throw new InvalidOperationException("Upload em Processing sem impressão digital.");
        var read = await ReadOriginalAsync(upload, cancellationToken);
        if (read.Bytes is not { } original)
        {
            return await AbandonAsync(upload, version, read.Problem, cancellationToken);
        }

        // O que chegou ao Worker tem de ser o que a recepção gravou. Objeto trocado ou corrompido na quarentena
        // nunca chega ao decoder.
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), fingerprint.Sha256.Span))
        {
            LogOriginalMismatch(_logger, upload.Id);
            return await AbandonAsync(upload, version, Settlement.OriginalMismatch, cancellationToken);
        }

        var outcome = await _sanitizer.SanitizeAsync(fingerprint.Format, original, cancellationToken);
        return outcome switch
        {
            SanitizeOutcome.Rejected rejected => await RejectAsync(upload, version, rejected.Reason, cancellationToken),
            SanitizeOutcome.Sanitized sanitized => await PublishAsync(upload, fingerprint, version, sanitized, cancellationToken),
            _ => throw new InvalidOperationException("Sanitização sem desfecho."),
        };
    }

    private async Task<(byte[]? Bytes, Settlement Problem)> ReadOriginalAsync(Upload upload, CancellationToken cancellationToken)
    {
        await using var stream = await _store.OpenReadAsync(ObjectKey.Parse(upload.QuarantineKey), cancellationToken);
        if (stream is null)
        {
            LogOriginalMissing(_logger, upload.Id);
            return (null, Settlement.OriginalMissing);
        }

        // O original tem no máximo 5 MiB (CHECK de size_bytes); lê só até o tamanho gravado. Tamanho diferente já é
        // outro objeto: mesma decisão do hash diferente.
        await using var exact = new ExactLengthStream(stream, upload.SizeBytes);
        using var copy = new MemoryStream((int)upload.SizeBytes);
        try
        {
            await exact.CopyToAsync(copy, cancellationToken);
        }
        catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException)
        {
            LogOriginalMismatch(_logger, upload.Id);
            return (null, Settlement.OriginalMismatch);
        }

        return (copy.ToArray(), Settlement.Delivered);
    }

    /// <summary>
    /// Falha que não se resolve tentando de novo: o upload termina em <c>Rejected PROCESSING_LIMIT</c>, para não
    /// ficar em <c>Processing</c> ocupando vaga com o original (e o EXIF dele) na quarentena. A entrega continua
    /// encerrada como <c>Failed</c> com a causa, que é o sinal para a operação.
    /// </summary>
    private async Task<Settlement> AbandonAsync(Upload upload, long version, Settlement problem, CancellationToken cancellationToken) =>
        await RejectAsync(upload, version, PhotoRejection.ProcessingLimit, cancellationToken) == Settlement.Delivered
            ? problem
            : Settlement.LeaseLost;

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
    /// tentativa os sobrescreve nas mesmas chaves (spec 16.2). Nunca se publica por ter gravado no storage. Antes de
    /// gravar, confere que o upload continua nesta tentativa: outra que o retomou ou decidiu durante a sanitização é
    /// dona das chaves, e esta só se retira.
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
        if (!await IsStillProcessingAtAsync(upload.Id, version, cancellationToken))
        {
            return Settlement.LeaseLost;
        }

        foreach (var variant in sanitized.Variants)
        {
            await using var content = new MemoryStream(variant.Content, writable: false);
            await _store.PutAsync(
                ObjectKey.ForDerivative(ObjectKey.Parse(upload.QuarantineKey), variant.Kind),
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
                ObjectKey.ForDerivative(ObjectKey.Parse(upload.QuarantineKey), variant.Kind),
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

    /// <summary>
    /// Toda tentativa que toma o upload incrementa a versão (<see cref="MoveAsync"/>), então a versão que esta tentativa
    /// gravou só continua lá se nenhuma outra o retomou nem decidiu.
    /// </summary>
    private Task<bool> IsStillProcessingAtAsync(Guid uploadId, long version, CancellationToken cancellationToken) =>
        _db.Set<Upload>().AnyAsync(
            u => u.Id == uploadId && u.Version == version && u.State == UploadState.Processing,
            cancellationToken);

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

    private async Task RetryOrAbandonAsync(
        ClaimedDelivery delivery,
        Guid uploadId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var cause = exception.GetType().Name;
        if (delivery.Attempts >= _options.MaxAttempts)
        {
            await GiveUpAsync(delivery, uploadId, cause, cancellationToken);
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

    /// <summary>
    /// Última tentativa: encerra o upload e a entrega como <c>Failed</c> com a causa. Se o banco ainda estiver fora,
    /// isto lança e a entrega fica sem encerrar: o lease vence, outro worker a retoma já acima do teto de tentativas e
    /// chega aqui de novo sem decodificar.
    /// </summary>
    private async Task GiveUpAsync(ClaimedDelivery delivery, Guid uploadId, string cause, CancellationToken cancellationToken)
    {
        LogGaveUp(_logger, delivery.Id, delivery.Attempts, cause);
        if (await AbandonAfterLastAttemptAsync(uploadId, cancellationToken))
        {
            await SettleAsync(delivery, OutboxDeliveryStatus.Failed, cause, cancellationToken);
        }
        else
        {
            LogLeaseLost(_logger, delivery.Id);
        }
    }

    /// <summary>
    /// Leva o upload a <c>Rejected</c> a partir de onde a última tentativa o deixou (<c>Quarantined</c>, se ela caiu
    /// antes de tomá-lo, ou <c>Processing</c>). Falso quando outra tentativa mexeu nele no meio.
    /// </summary>
    private async Task<bool> AbandonAfterLastAttemptAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await _db.Set<Upload>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == uploadId, cancellationToken);
        if (upload is null || upload.State is not (UploadState.Quarantined or UploadState.Processing))
        {
            return true;
        }

        var version = upload.Version;
        if (upload.State is UploadState.Quarantined)
        {
            if (!await MoveAsync(upload.Id, version, UploadState.Processing, rejection: null, cancellationToken))
            {
                return false;
            }

            version++;
        }

        if (await RejectAsync(upload, version, PhotoRejection.ProcessingLimit, cancellationToken) != Settlement.Delivered)
        {
            return false;
        }

        // A tentativa pode ter caído entre um derivado gravado e o seguinte; nenhum deles será referenciado.
        foreach (var variant in PhotoVariants.All)
        {
            await DeleteQuietlyAsync(ObjectKey.ForDerivative(ObjectKey.Parse(upload.QuarantineKey), variant), upload.Id);
        }

        return true;
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

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Original do upload {UploadId} difere do que a recepção gravou (tamanho ou SHA-256); rejeitado sem decodificar")]
    private static partial void LogOriginalMismatch(ILogger logger, Guid uploadId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Original do upload {UploadId} não está na quarentena; rejeitado")]
    private static partial void LogOriginalMissing(ILogger logger, Guid uploadId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Processamento da entrega {DeliveryId} falhou na tentativa {Attempt} ({Cause}); nova tentativa em {RetryAt}")]
    private static partial void LogWillRetry(ILogger logger, Guid deliveryId, int attempt, string cause, DateTimeOffset retryAt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Processamento da entrega {DeliveryId} falhou na última tentativa {Attempt} ({Cause}); upload rejeitado")]
    private static partial void LogGaveUp(ILogger logger, Guid deliveryId, int attempt, string cause);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Entrega {DeliveryId} de mídia esgotou {Attempts} tentativas e foi encerrada no claim; upload {UploadId} rejeitado")]
    private static partial void LogExhaustedAtClaim(ILogger logger, Guid deliveryId, int attempts, Guid uploadId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Entrega {DeliveryId} de mídia passou do prazo sem processar; upload {UploadId} rejeitado")]
    private static partial void LogExpired(ILogger logger, Guid deliveryId, Guid uploadId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entrega {DeliveryId} de mídia não foi encerrada por este worker: lease vencido ou upload já tomado")]
    private static partial void LogLeaseLost(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Objeto do upload {UploadId} não foi apagado ({Cause}); fica para a limpeza de órfãos")]
    private static partial void LogOrphanLeft(ILogger logger, Guid uploadId, string cause);
}
