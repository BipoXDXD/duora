using Duora.Core.Identity;
using Duora.Core.Media;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.Infrastructure.Media;

/// <summary>Pedido de upload já validado na borda (tipo, tamanho, chave e situação da conta).</summary>
public sealed record PhotoUploadCommand(Guid OwnerId, IdempotencyKey Key, PhotoFormat Format, long SizeBytes);

public abstract record PhotoUploadResult
{
    /// <summary>Upload novo em quarentena, ou o mesmo pedido repetido (estado atual do primeiro).</summary>
    public sealed record Accepted(Guid PhotoId, UploadState State) : PhotoUploadResult;

    /// <summary>A mesma chave ainda está transmitindo o primeiro envio.</summary>
    public sealed record InProgress : PhotoUploadResult;

    public sealed record KeyReused : PhotoUploadResult;

    public sealed record PendingLimitReached : PhotoUploadResult;

    /// <summary>A conta da sessão não existe mais.</summary>
    public sealed record OwnerMissing : PhotoUploadResult;

    /// <summary>A conta mudou de situação depois da checagem da borda (relida sob a guarda).</summary>
    public sealed record AccountRestricted : PhotoUploadResult;

    /// <summary>O corpo chegou curto, longo, tarde demais ou a conexão caiu.</summary>
    public sealed record BodyIncomplete : PhotoUploadResult;

    public sealed record StorageFailed : PhotoUploadResult;
}

/// <summary>Estado de um upload como o titular o vê em API-M02.</summary>
public sealed record PhotoStatus(Guid PhotoId, UploadState State, string? RejectionCode);

/// <summary>
/// Recepção de fotos (API-M01) e leitura do estado (API-M02), ADR-0025. Três passos, sem transação aberta durante o
/// streaming: (1) reserva <c>Pending</c> sob lock da linha do dono, que serializa contagem do teto e idempotência;
/// (2) transmite o corpo para a quarentena calculando o SHA-256; (3) promove a <c>Quarantined</c> e grava o pedido de
/// processamento na outbox na mesma transação. Falha no passo 2 desfaz a reserva e apaga o objeto parcial.
/// </summary>
public sealed partial class PhotoUploads
{
    private static readonly TimeSpan CompensationTimeout = TimeSpan.FromSeconds(5);

    private readonly DuoraDbContext _db;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;
    private readonly IOptions<MediaOptions> _options;
    private readonly ILogger<PhotoUploads> _logger;

    internal PhotoUploads(
        DuoraDbContext db,
        IObjectStore store,
        TimeProvider time,
        IOptions<MediaOptions> options,
        ILogger<PhotoUploads> logger)
    {
        _db = db;
        _store = store;
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>Falso sem <c>Media:Review</c>: a API responde 503 e nada é gravado (ADR-0025).</summary>
    public bool UploadsEnabled => Options.UploadsEnabled;

    public double MinUploadBytesPerSecond => Options.MinUploadBytesPerSecond;

    public TimeSpan MinUploadRateGracePeriod => Options.MinUploadRateGracePeriod;

    private MediaOptions Options => _options.Value;

    public async Task<PhotoUploadResult> ReceiveAsync(PhotoUploadCommand command, Stream body, CancellationToken cancellationToken)
    {
        var reservation = await ReserveAsync(command, cancellationToken);
        return reservation switch
        {
            Reservation.Reserved reserved => await StoreAsync(reserved.Upload, command, body, cancellationToken),
            Reservation.Existing existing => await ReplayAsync(existing.Upload, command, body, cancellationToken),
            Reservation.Refused refused => refused.Result,
            _ => throw new InvalidOperationException("Reserva sem desfecho."),
        };
    }

    public Task<PhotoStatus?> FindOwnStatusAsync(Guid ownerId, Guid photoId, CancellationToken cancellationToken) =>
        _db.Set<Upload>()
            .AsNoTracking()
            .Where(upload => upload.Id == photoId && upload.OwnerId == ownerId)
            .Select(upload => new PhotoStatus(upload.Id, upload.State, upload.RejectionCode))
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<Reservation> ReserveAsync(PhotoUploadCommand command, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var abandonedBefore = now - Options.AbandonedReservationAfter;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockGuardAsync(command.OwnerId, cancellationToken))
        {
            return new Reservation.Refused(new PhotoUploadResult.OwnerMissing());
        }

        // Relida sob a guarda: uma restrição aplicada entre a checagem da borda e aqui vale para este envio.
        var status = await _db.Set<DuoraUser>()
            .Where(user => user.Id == command.OwnerId)
            .Select(user => (AccountStatus?)user.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (status is not { } current)
        {
            return new Reservation.Refused(new PhotoUploadResult.OwnerMissing());
        }

        if (!PhotoUploadPolicy.Allows(current))
        {
            return new Reservation.Refused(new PhotoUploadResult.AccountRestricted());
        }

        var uploads = _db.Set<Upload>();
        var existing = await uploads.SingleOrDefaultAsync(
            upload => upload.OwnerId == command.OwnerId && upload.ClientKey == command.Key.Value,
            cancellationToken);
        string? abandonedKey = null;
        if (existing is not null)
        {
            if (existing.State != UploadState.Pending)
            {
                return new Reservation.Existing(existing);
            }

            if (existing.CreatedAt >= abandonedBefore)
            {
                return new Reservation.Refused(new PhotoUploadResult.InProgress());
            }

            // Reserva abandonada: o envio original já estourou o prazo. Sai para a mesma chave valer de novo.
            abandonedKey = existing.QuarantineKey;
            uploads.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var pending = await uploads.CountAsync(
            upload => upload.OwnerId == command.OwnerId
                && (upload.State == UploadState.Quarantined
                    || upload.State == UploadState.Processing
                    || (upload.State == UploadState.Pending && upload.CreatedAt >= abandonedBefore)),
            cancellationToken);
        if (!Options.Quota.Admits(pending))
        {
            return new Reservation.Refused(new PhotoUploadResult.PendingLimitReached());
        }

        var reserved = Upload.Reserve(command.OwnerId, command.Key, command.Format, command.SizeBytes, now);
        uploads.Add(reserved);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (abandonedKey is not null)
        {
            await DeleteQuietlyAsync(ObjectKey.Parse(abandonedKey), reserved.Id);
        }

        return new Reservation.Reserved(reserved);
    }

    /// <summary>
    /// Guarda do Media por dono (ADR-0025, concurrency.md): cria a linha se faltar e a trava com <c>FOR UPDATE</c>.
    /// Dois envios da mesma conta esperam um pelo outro aqui, então a contagem do teto sempre vê a reserva do anterior
    /// (sem write skew), sem travar tabela de outro módulo. Conta inexistente é a FK recusando o insert (23503).
    /// </summary>
    private async Task<bool> LockGuardAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        try
        {
            await _db.Database.ExecuteSqlAsync(
                $"insert into media.upload_guards (owner_id) values ({ownerId}) on conflict do nothing",
                cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return false;
        }

        var locked = await _db.Database
            .SqlQuery<Guid>($"select owner_id as \"Value\" from media.upload_guards where owner_id = {ownerId} for update")
            .ToListAsync(cancellationToken);
        return locked.Count == 1;
    }

    private async Task<PhotoUploadResult> StoreAsync(
        Upload upload,
        PhotoUploadCommand command,
        Stream body,
        CancellationToken cancellationToken)
    {
        var key = ObjectKey.Parse(upload.QuarantineKey);
        await using var counted = new UploadBodyStream(body, command.SizeBytes);
        using var timeout = new CancellationTokenSource(Options.UploadTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _store.PutAsync(key, counted, command.SizeBytes, PhotoFormats.ContentType(command.Format), linked.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Compensação (spec 16.2): a reserva e o objeto parcial saem; o erro não some, vira o resultado e o log.
            var clientFailed = counted.ClientFailed
                || linked.IsCancellationRequested
                || (IsTimeout(exception) && !counted.IsComplete);
            LogStreamingFailed(_logger, upload.Id, clientFailed, exception.GetType().Name);
            await DiscardReservationAsync(upload, key);
            return clientFailed ? new PhotoUploadResult.BodyIncomplete() : new PhotoUploadResult.StorageFailed();
        }

        return await QuarantineAsync(upload, key, counted.Sha256);
    }

    /// <summary>
    /// Sem o token da requisição: depois que os bytes estão no storage, a queda do cliente não desfaz o upload
    /// (spec 8.1). Zero linhas significa que a reserva foi reaproveitada por um reenvio depois do prazo.
    /// </summary>
    private async Task<PhotoUploadResult> QuarantineAsync(Upload upload, ObjectKey key, byte[] sha256)
    {
        var now = _time.GetUtcNow();
        await using var transaction = await _db.Database.BeginTransactionAsync(CancellationToken.None);
        var promoted = await _db.Set<Upload>()
            .Where(u => u.Id == upload.Id && u.State == UploadState.Pending && u.Version == upload.Version)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(u => u.State, UploadState.Quarantined)
                    .SetProperty(u => u.ContentSha256, sha256)
                    .SetProperty(u => u.Version, u => u.Version + 1),
                CancellationToken.None);
        if (promoted == 0)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            LogReservationLost(_logger, upload.Id);
            await DeleteQuietlyAsync(key, upload.Id);
            return new PhotoUploadResult.StorageFailed();
        }

        _db.Add(MediaProcessingOutbox.Create(upload.Id, now));
        await _db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        LogQuarantined(_logger, upload.Id);
        return new PhotoUploadResult.Accepted(upload.Id, UploadState.Quarantined);
    }

    /// <summary>
    /// Mesma chave de um upload que já passou do streaming: o corpo é lido de novo só para o hash, sem storage, e
    /// comparado com a impressão digital gravada (spec 8.7).
    /// </summary>
    private async Task<PhotoUploadResult> ReplayAsync(
        Upload existing,
        PhotoUploadCommand command,
        Stream body,
        CancellationToken cancellationToken)
    {
        await using var counted = new UploadBodyStream(body, command.SizeBytes);
        using var timeout = new CancellationTokenSource(Options.UploadTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await counted.CopyToAsync(Stream.Null, linked.Token);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
        {
            return new PhotoUploadResult.BodyIncomplete();
        }

        var stored = existing.Fingerprint()
            ?? throw new InvalidOperationException("Upload fora de Pending sem impressão digital gravada.");
        var incoming = new UploadFingerprint(command.Format, command.SizeBytes, counted.Sha256);
        return UploadFingerprint.Compare(stored, incoming) == IdempotentOutcome.Replay
            ? new PhotoUploadResult.Accepted(existing.Id, existing.State)
            : new PhotoUploadResult.KeyReused();
    }

    /// <summary>
    /// Timeout do SDK enquanto o corpo ainda não chegou inteiro: quem estava lento era o cliente, não o storage.
    /// </summary>
    private static bool IsTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private async Task DiscardReservationAsync(Upload upload, ObjectKey key)
    {
        await _db.Set<Upload>()
            .Where(u => u.Id == upload.Id && u.State == UploadState.Pending)
            .ExecuteDeleteAsync(CancellationToken.None);
        await DeleteQuietlyAsync(key, upload.Id);
    }

    /// <summary>
    /// Apagar o objeto é melhor esforço aqui: se o storage também falhar, o objeto fica sem referência e é caso da
    /// limpeza de órfãos (spec 16.2). A falha é registrada, nunca engolida em silêncio.
    /// </summary>
    private async Task DeleteQuietlyAsync(ObjectKey key, Guid uploadId)
    {
        // Timeout curto: a compensação não pode prender a resposta (nem o Worker) num storage travado.
        using var timeout = new CancellationTokenSource(CompensationTimeout, _time);
        try
        {
            await _store.DeleteAsync(key, timeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogOrphanLeft(_logger, uploadId, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload {UploadId} recebido em quarentena")]
    private static partial void LogQuarantined(ILogger logger, Guid uploadId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Upload {UploadId} não foi recebido (falha do cliente: {ClientFailed}, causa {Cause}); reserva desfeita")]
    private static partial void LogStreamingFailed(ILogger logger, Guid uploadId, bool clientFailed, string cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reserva do upload {UploadId} sumiu antes da quarentena; objeto descartado")]
    private static partial void LogReservationLost(ILogger logger, Guid uploadId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Objeto do upload {UploadId} não foi apagado ({Cause}); fica para a limpeza de órfãos")]
    private static partial void LogOrphanLeft(ILogger logger, Guid uploadId, string cause);

    private abstract record Reservation
    {
        public sealed record Reserved(Upload Upload) : Reservation;

        public sealed record Existing(Upload Upload) : Reservation;

        public sealed record Refused(PhotoUploadResult Result) : Reservation;
    }
}
