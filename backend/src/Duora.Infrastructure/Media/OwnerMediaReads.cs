using System.Data;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Duora.Infrastructure.Media;

/// <summary>Bytes de um derivado aprovado já autorizado; quem recebe transmite e descarta.</summary>
public sealed class AuthorizedMedia(Stream content) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// API-M04: leitura de derivado de asset <c>Approved</c>, hoje só pelo titular. Não há seleção de foto de perfil nem
/// contexto de sala que autorize terceiros; quando houver, entra aqui com a própria regra. A autorização é relida no
/// banco a cada pedido, antes de abrir o objeto e de novo depois de abri-lo, antes do primeiro byte: uma exclusão
/// confirmada no meio do caminho não deixa o conteúdo sair. Nenhum I/O de storage acontece com transação aberta. O
/// derivado promovido só sai se tamanho e SHA-256 conferirem com o que a promoção gravou.
/// </summary>
public sealed partial class OwnerMediaReads
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<OwnerMediaReads> _logger;

    internal OwnerMediaReads(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time,
        ILogger<OwnerMediaReads> logger)
    {
        _contexts = contexts;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Nulo para alheio, inexistente, não aprovado, excluído, sem arquivo ou com bytes que não conferem com o digest
    /// gravado: o chamador responde o mesmo 404.
    /// </summary>
    public async Task<AuthorizedMedia?> OpenAsync(Guid ownerId, Guid sessionId, Guid assetId, PhotoVariant variant,
        CancellationToken cancellationToken)
    {
        if (await AuthorizedVariantAsync(new PhotoReviewActor(ownerId, sessionId), assetId, variant, cancellationToken)
            is not { } stored)
        {
            return null;
        }

        var key = ObjectKey.Parse(stored.ObjectKey);
        var opened = await _store.OpenReadAsync(key, cancellationToken);
        if (opened is null)
        {
            LogMissing(_logger, assetId, PhotoVariants.Name(variant));
            return null;
        }

        if (await VerifiedAsync(opened, stored, assetId, PhotoVariants.Name(variant), cancellationToken)
            is not { } content)
        {
            return null;
        }

        if (!await StillApprovedAsync(ownerId, assetId, key, cancellationToken))
        {
            await content.DisposeAsync();
            return null;
        }

        return new AuthorizedMedia(content);
    }

    /// <summary>
    /// O derivado promovido é lido inteiro e conferido contra o tamanho e o SHA-256 de
    /// <c>media.review_promotion_variants</c> antes do primeiro byte; o teto de 16 MiB por variante vem do CHECK
    /// dessa tabela. O legado nunca teve digest persistido e segue entregue, com registro para ser contado.
    /// </summary>
    private async Task<Stream?> VerifiedAsync(Stream opened, StoredVariant stored, Guid assetId, string variant,
        CancellationToken cancellationToken)
    {
        if (stored is { Sha256: { } expected, SizeBytes: { } length })
        {
            await using (opened)
            {
                if (await ReadMatchingAsync(opened, length, expected, cancellationToken) is { } verified)
                {
                    return verified;
                }
            }

            LogDigestMismatch(_logger, assetId, variant);
            return null;
        }

        if (stored.RequiresDigest)
        {
            await opened.DisposeAsync();
            LogDigestMissing(_logger, assetId, variant);
            return null;
        }

        LogServedWithoutDigest(_logger, assetId, variant);
        return opened;
    }

    private static async Task<MemoryStream?> ReadMatchingAsync(Stream source, long length, byte[] expected,
        CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream(checked((int)length));
        try
        {
            await using (var exact = new ExactLengthStream(source, length))
            {
                await exact.CopyToAsync(buffer, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException)
        {
            await buffer.DisposeAsync();
            return null;
        }

        var hash = SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
        if (!CryptographicOperations.FixedTimeEquals(hash, expected))
        {
            await buffer.DisposeAsync();
            return null;
        }

        buffer.Position = 0;
        return buffer;
    }

    /// <summary>
    /// A linha de digest é a da geração que promoveu este asset e desta chave. Exige-se digest quando o asset veio de
    /// uma promoção ou quando a chave tem o formato promovido: sem a linha, é vínculo quebrado, não legado.
    /// </summary>
    private async Task<StoredVariant?> AuthorizedVariantAsync(PhotoReviewActor owner, Guid assetId, PhotoVariant variant,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await PhotoOwnerAccess.AuthorizeAsync(db, _time, owner, cancellationToken))
        {
            return null;
        }

        var kind = PhotoVariants.Name(variant);
        var stored = await db.Database.SqlQuery<StoredVariant>($"""
            select v.object_key as "ObjectKey", pv.size_bytes as "SizeBytes", pv.sha256 as "Sha256",
              (p.generation is not null or starts_with(v.object_key, 'derivatives/review-')) as "RequiresDigest"
            from media.media_assets a
            join media.uploads u on u.id = a.upload_id and u.owner_id = a.owner_id
            join media.media_variants v on v.media_asset_id = a.id and v.kind = {kind}
            left join media.review_promotions p on p.asset_id = a.id
            left join media.review_promotion_variants pv
              on pv.generation = p.generation and pv.kind = v.kind and pv.object_key = v.object_key
            where a.id = {assetId} and a.owner_id = {owner.UserId}
              and a.state = 'Approved' and a.deleted_at is null and u.state = 'Approved'
              and not exists (select 1 from media.photo_tombstones t where t.photo_id = u.id)
            """).SingleOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return stored;
    }

    private async Task<bool> StillApprovedAsync(Guid ownerId, Guid assetId, ObjectKey key, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        return await db.Database.SqlQuery<bool>($"""
            select exists (
              select 1 from media.media_assets a
              join media.uploads u on u.id = a.upload_id and u.owner_id = a.owner_id
              join media.media_variants v on v.media_asset_id = a.id and v.object_key = {key.Value}
              where a.id = {assetId} and a.owner_id = {ownerId}
                and a.state = 'Approved' and a.deleted_at is null and u.state = 'Approved') as "Value"
            """).SingleAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Derivado {Variant} do asset aprovado {AssetId} não está no storage")]
    private static partial void LogMissing(ILogger logger, Guid assetId, string variant);

    [LoggerMessage(Level = LogLevel.Error, Message = "Derivado {Variant} do asset aprovado {AssetId} não confere com o digest gravado")]
    private static partial void LogDigestMismatch(ILogger logger, Guid assetId, string variant);

    [LoggerMessage(Level = LogLevel.Error, Message = "Derivado promovido {Variant} do asset aprovado {AssetId} sem linha de digest")]
    private static partial void LogDigestMissing(ILogger logger, Guid assetId, string variant);

    [LoggerMessage(Level = LogLevel.Information, Message = "Derivado legado {Variant} do asset aprovado {AssetId} entregue sem digest")]
    private static partial void LogServedWithoutDigest(ILogger logger, Guid assetId, string variant);

    private sealed record StoredVariant(string ObjectKey, long? SizeBytes, byte[]? Sha256, bool RequiresDigest);
}
