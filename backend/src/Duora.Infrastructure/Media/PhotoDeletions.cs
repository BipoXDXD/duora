using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.Infrastructure.Media;

public abstract record PhotoDeletionResult
{
    /// <summary>Exclusão confirmada agora ou numa repetição da mesma intenção; a versão é a da foto depois do efeito.</summary>
    public sealed record Deleted(long PhotoVersion) : PhotoDeletionResult;

    /// <summary>A sessão não pertence mais a um participante que pode agir sobre as próprias fotos.</summary>
    public sealed record Forbidden : PhotoDeletionResult;

    /// <summary>Foto inexistente ou de outra conta: as duas respostas são iguais.</summary>
    public sealed record NotFound : PhotoDeletionResult;

    public sealed record PreconditionFailed : PhotoDeletionResult;

    public sealed record KeyReused : PhotoDeletionResult;

    /// <summary>Os bytes do original ainda estão chegando (<c>Pending</c>): a recepção é dona deles até terminar.</summary>
    public sealed record StateConflict : PhotoDeletionResult;
}

/// <summary>
/// API-M03: exclusão lógica pelo titular numa transação curta READ COMMITTED, sem I/O de storage. Ordem de locks
/// (modules.md): guarda Safety do dono, Identity e sessão (via <see cref="PhotoOwnerAccess.AuthorizeAsync"/>),
/// depois a foto e o asset. Na mesma transação: foto e asset em <c>Deleted</c>, tombstone, obrigações de limpeza das
/// chaves exatas, recibo idempotente e auditoria. A remoção dos bytes é do <see cref="PhotoDeletionCleanupDispatcher"/>
/// e, para o candidato privado da revisão humana, do <see cref="DecidedCandidateCleanupDispatcher"/>. Tentativas ainda
/// não confirmadas (candidato em preparo, promoção da aprovação) ficam com os coletores de preparo, que não dependem do
/// estado da foto; o Worker e o moderador que chegarem depois encontram a foto <c>Deleted</c> e não publicam nada.
/// O tombstone é só do banco: o ledger externo que sobrevive a um restore ainda não existe (docs/api/media-review.md).
/// </summary>
public sealed partial class PhotoDeletions
{
    private static readonly string[] DeletableStates =
    [
        nameof(UploadState.Quarantined), nameof(UploadState.Processing), nameof(UploadState.AwaitingReview),
        nameof(UploadState.Approved), nameof(UploadState.Rejected),
    ];

    /// <summary>Estados em que um Worker ainda pode gravar os derivados determinísticos da política TechnicalOnly.</summary>
    private static readonly string[] TechnicalWriterStates = [nameof(UploadState.Quarantined), nameof(UploadState.Processing)];

    /// <summary>
    /// Folga para um Worker que já passou da checagem de versão terminar de gravar os derivados antes da limpeza
    /// (lease de processamento de 2 min mais margem). Não existe coletor de órfãos: um PutAsync que termine depois
    /// desta folga deixa o derivado sem obrigação. O risco é só do TechnicalOnly (Development/Testing), cujo Worker
    /// confere a versão antes de gravar; a HumanReview grava candidatos com geração própria e coletor de preparo.
    /// </summary>
    internal static readonly TimeSpan InFlightWriterGrace = TimeSpan.FromMinutes(15);

    private readonly IDbContextFactory<DuoraDbContext> _contexts;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;
    private readonly IOptions<PhotoDeletionOptions> _options;
    private readonly ILogger<PhotoDeletions> _logger;

    internal PhotoDeletions(
        IDbContextFactory<DuoraDbContext> contexts,
        IObjectStore store,
        TimeProvider time,
        IOptions<PhotoDeletionOptions> options,
        ILogger<PhotoDeletions> logger)
    {
        _contexts = contexts;
        _store = store;
        _time = time;
        _options = options;
        _logger = logger;
    }

    public async Task<PhotoDeletionResult> DeleteAsync(
        Guid ownerId,
        Guid sessionId,
        Guid photoId,
        long expectedVersion,
        IdempotencyKey key,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedVersion, 1);
        try
        {
            return await AttemptAsync(new PhotoReviewActor(ownerId, sessionId), photoId, expectedVersion, key, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Duas chamadas com a mesma chave passaram juntas pela leitura do recibo: a segunda relê o recibo vencedor.
            return await AttemptAsync(new PhotoReviewActor(ownerId, sessionId), photoId, expectedVersion, key, cancellationToken);
        }
    }

    private async Task<PhotoDeletionResult> AttemptAsync(
        PhotoReviewActor owner,
        Guid photoId,
        long expectedVersion,
        IdempotencyKey key,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await PhotoOwnerAccess.AuthorizeAsync(db, _time, owner, cancellationToken))
        {
            return new PhotoDeletionResult.Forbidden();
        }

        var photo = await db.Database.SqlQuery<PhotoRow>($"""
            select id as "Id", state as "State", version as "Version", quarantine_key as "QuarantineKey"
            from media.uploads where id = {photoId} and owner_id = {owner.UserId} for update
            """).SingleOrDefaultAsync(cancellationToken);
        var operationKey = OperationKeyOf(owner.UserId, key, photoId, expectedVersion);

        // A mesma chave confirmada responde antes de comparar a versão atual, que a própria exclusão já avançou.
        if (await ReceiptAsync(db, operationKey, cancellationToken) is { } receipt)
        {
            return Replay(receipt, owner.UserId, key, photoId, expectedVersion);
        }

        if (photo is null)
        {
            return new PhotoDeletionResult.NotFound();
        }

        if (photo.Version != expectedVersion)
        {
            return new PhotoDeletionResult.PreconditionFailed();
        }

        if (photo.State == nameof(UploadState.Deleted))
        {
            await RecordAsync(db, owner.UserId, key, photo, Snapshot(photo, asset: null, []), photo.Version, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PhotoDeletionResult.Deleted(photo.Version);
        }

        if (!DeletableStates.Contains(photo.State))
        {
            return new PhotoDeletionResult.StateConflict();
        }

        var asset = await db.Database.SqlQuery<AssetRow>($"""
            select id as "Id", state as "State", version as "Version"
            from media.media_assets where upload_id = {photoId} for update
            """).SingleOrDefaultAsync(cancellationToken);
        var variantKeys = asset is null
            ? []
            : await db.Database.SqlQuery<string>($"""
                select object_key as "Value" from media.media_variants where media_asset_id = {asset.Id} order by kind
                """).ToListAsync(cancellationToken);

        var deletedVersion = checked(photo.Version + 1);
        var binding = await RecordAsync(db, owner.UserId, key, photo, Snapshot(photo, asset, variantKeys), deletedVersion, cancellationToken);
        await MarkDeletedAsync(db, photo, asset, deletedVersion, _time.GetUtcNow(), cancellationToken);
        await EnqueueCleanupAsync(db, photo, binding, deletedVersion, variantKeys, cancellationToken);
        await new AuditTrail(db, _time).AppendAsync(
            new AuditRecord(AuditActorKind.User, owner.UserId, AuditAction.MediaPhotoDeleted, AuditTargetTypes.Photo, photoId, deletedVersion),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogDeleted(_logger, photoId);
        return new PhotoDeletionResult.Deleted(deletedVersion);
    }

    private static async Task MarkDeletedAsync(DuoraDbContext db, PhotoRow photo, AssetRow? asset, long deletedVersion,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var photos = await db.Database.ExecuteSqlAsync($"""
            update media.uploads set state = 'Deleted', rejection_code = null, version = {deletedVersion}
            where id = {photo.Id} and state = {photo.State} and version = {photo.Version}
            """, cancellationToken);
        var assets = asset is null || asset.State == nameof(MediaAssetState.Deleted)
            ? 1
            : await db.Database.ExecuteSqlAsync($"""
                update media.media_assets set state = 'Deleted', deleted_at = {now}, version = version + 1
                where id = {asset.Id} and version = {asset.Version}
                """, cancellationToken);
        if (photos != 1 || assets != 1)
        {
            throw new InvalidOperationException("A exclusão precisa mudar exatamente uma foto e um asset travados.");
        }
    }

    /// <summary>
    /// Só chaves exatas desta foto. O original entra quando não há obrigação própria pendente em
    /// <c>media.original_cleanup</c>; apagar o que já não existe é sucesso no storage.
    /// </summary>
    private async Task EnqueueCleanupAsync(DuoraDbContext db, PhotoRow photo, PhotoDeletionIntentBinding binding,
        long deletedVersion, List<string> variantKeys, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var intent = Convert.FromHexString(binding.IntentSha256);
        await db.Database.ExecuteSqlAsync($"""
            insert into media.photo_tombstones (photo_id, owner_id, operation_key, intent_sha256, deleted_version, deleted_at)
            select id, owner_id, {binding.OperationKey}, {intent}, {deletedVersion}, {now} from media.uploads where id = {photo.Id}
            """, cancellationToken);
        foreach (var objectKey in variantKeys)
        {
            await db.Database.ExecuteSqlAsync($"""
                insert into media.photo_deletion_cleanup (object_key, photo_id, created_at, available_at)
                values ({ObjectKey.Parse(objectKey).Value}, {photo.Id}, {now}, {now})
                """, cancellationToken);
        }

        await db.Database.ExecuteSqlAsync($"""
            insert into media.photo_deletion_cleanup (object_key, photo_id, created_at, available_at)
            select {ObjectKey.Parse(photo.QuarantineKey).Value}, {photo.Id}, {now}, {now}
            where not exists (select 1 from media.original_cleanup where upload_id = {photo.Id})
            """, cancellationToken);

        if (variantKeys.Count == 0 && TechnicalWriterStates.Contains(photo.State))
        {
            var quarantine = ObjectKey.Parse(photo.QuarantineKey);
            foreach (var variant in PhotoVariants.All)
            {
                await db.Database.ExecuteSqlAsync($"""
                    insert into media.photo_deletion_cleanup (object_key, photo_id, created_at, available_at)
                    values ({ObjectKey.ForDerivative(quarantine, variant).Value}, {photo.Id}, {now}, {now + InFlightWriterGrace})
                    """, cancellationToken);
            }
        }

        // O candidato confirmado da revisão humana: o tombstone explica a obrigação; um marcador anterior (expiração,
        // indisponibilidade, decisão) é mantido e assumido, porque aqueles coletores leem o estado que acabou de mudar.
        await db.Database.ExecuteSqlAsync($"""
            update media.review_candidates
            set cleanup_available_at = case when cleanup_requested_at is null then least(cleanup_available_at, {now})
                  else cleanup_available_at end,
                cleanup_requested_at = coalesce(cleanup_requested_at, {now})
            where upload_id = {photo.Id} and committed_at is not null and deleted_at is null
            """, cancellationToken);
    }

    private async Task<PhotoDeletionIntentBinding> RecordAsync(DuoraDbContext db, Guid ownerId, IdempotencyKey key,
        PhotoRow photo, byte[] snapshot, long resultVersion, CancellationToken cancellationToken)
    {
        var binding = Bind(ownerId, key, photo.Id, photo.Version, snapshot);
        var intent = Convert.FromHexString(binding.IntentSha256);
        await db.Database.ExecuteSqlAsync($"""
            insert into media.photo_deletion_commands (operation_key, owner_id, key_id, photo_id, intent_sha256,
              snapshot_sha256, expected_version, result_version, created_at)
            values ({binding.OperationKey}, {ownerId}, {binding.IdempotencyKeyId}, {photo.Id}, {intent}, {snapshot},
              {photo.Version}, {resultVersion}, {_time.GetUtcNow()})
            """, cancellationToken);
        return binding;
    }

    private static Task<Receipt?> ReceiptAsync(DuoraDbContext db, string operationKey, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<Receipt>($"""
            select owner_id as "OwnerId", key_id as "KeyId", intent_sha256 as "Intent", snapshot_sha256 as "Snapshot",
              result_version as "ResultVersion"
            from media.photo_deletion_commands where operation_key = {operationKey}
            """).SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Recalcula a intenção com o snapshot gravado na primeira vez: só a mesma foto e a mesma versão esperada
    /// reproduzem o hash. Qualquer outra combinação com a chave é conflito, nunca uma nova exclusão.
    /// </summary>
    private PhotoDeletionResult Replay(Receipt receipt, Guid ownerId, IdempotencyKey key, Guid photoId, long expectedVersion)
    {
        if (receipt.OwnerId != ownerId || receipt.KeyId != _options.Value.OperationKeyId)
        {
            return new PhotoDeletionResult.KeyReused();
        }

        var intent = Convert.FromHexString(Bind(ownerId, key, photoId, expectedVersion, receipt.Snapshot).IntentSha256);
        return CryptographicOperations.FixedTimeEquals(intent, receipt.Intent)
            ? new PhotoDeletionResult.Deleted(receipt.ResultVersion)
            : new PhotoDeletionResult.KeyReused();
    }

    /// <summary>
    /// A chave de operação V1 cobre só ID da chave, ambiente, titular e chave do cliente; foto, versão e snapshot
    /// entram apenas na intenção. Por isso o recibo pode ser buscado antes de capturar o snapshot.
    /// </summary>
    private string OperationKeyOf(Guid ownerId, IdempotencyKey key, Guid photoId, long expectedVersion) =>
        Bind(ownerId, key, photoId, expectedVersion, new byte[SHA256.HashSizeInBytes]).OperationKey;

    private PhotoDeletionIntentBinding Bind(Guid ownerId, IdempotencyKey key, Guid photoId, long expectedVersion, byte[] snapshot)
    {
        var options = _options.Value;
        var material = options.TryReadKey()
            ?? throw new InvalidOperationException("Media:Deletion:OperationKey ausente; a validação do boot deveria ter barrado.");
        try
        {
            return PhotoDeletionIntentBinding.Create(options.EnvironmentId, ownerId, key, material, options.OperationKeyId,
                photoId, expectedVersion, snapshot);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    /// <summary>Estado capturado sob os locks: foto, asset e chaves exatas que a exclusão vai revogar e limpar.</summary>
    private static byte[] Snapshot(PhotoRow photo, AssetRow? asset, IReadOnlyList<string> variantKeys)
    {
        var text = new StringBuilder("DUORA-PHOTO-DELETE-SNAPSHOT-V1\n")
            .Append(CultureInfo.InvariantCulture, $"{photo.Id:D}\n{photo.State}\n{photo.Version}\n{photo.QuarantineKey}\n")
            .Append(CultureInfo.InvariantCulture, $"{asset?.Id.ToString("D") ?? "-"}\n{asset?.State ?? "-"}\n{asset?.Version ?? 0}\n");
        foreach (var variantKey in variantKeys)
        {
            text.Append(variantKey).Append('\n');
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Foto {PhotoId} excluída pelo titular; limpeza física enfileirada")]
    private static partial void LogDeleted(ILogger logger, Guid photoId);

    private sealed record PhotoRow(Guid Id, string State, long Version, string QuarantineKey);

    private sealed record AssetRow(Guid Id, string State, long Version);

    private sealed record Receipt(Guid OwnerId, string KeyId, byte[] Intent, byte[] Snapshot, long ResultVersion);
}
