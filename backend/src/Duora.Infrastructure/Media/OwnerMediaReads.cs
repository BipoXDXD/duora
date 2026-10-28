using System.Data;
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
/// confirmada no meio do caminho não deixa o conteúdo sair. Nenhum I/O de storage acontece com transação aberta.
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

    /// <summary>Nulo para alheio, inexistente, não aprovado, excluído ou sem arquivo: o chamador responde o mesmo 404.</summary>
    public async Task<AuthorizedMedia?> OpenAsync(Guid ownerId, Guid sessionId, Guid assetId, PhotoVariant variant,
        CancellationToken cancellationToken)
    {
        if (await AuthorizedKeyAsync(new PhotoReviewActor(ownerId, sessionId), assetId, variant, cancellationToken) is not { } key)
        {
            return null;
        }

        var content = await _store.OpenReadAsync(key, cancellationToken);
        if (content is null)
        {
            LogMissing(_logger, assetId, PhotoVariants.Name(variant));
            return null;
        }

        if (!await StillApprovedAsync(ownerId, assetId, key, cancellationToken))
        {
            await content.DisposeAsync();
            return null;
        }

        return new AuthorizedMedia(content);
    }

    private async Task<ObjectKey?> AuthorizedKeyAsync(PhotoReviewActor owner, Guid assetId, PhotoVariant variant,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await PhotoOwnerAccess.AuthorizeAsync(db, _time, owner, cancellationToken))
        {
            return null;
        }

        var kind = PhotoVariants.Name(variant);
        var stored = await db.Database.SqlQuery<string>($"""
            select v.object_key as "Value"
            from media.media_assets a
            join media.uploads u on u.id = a.upload_id and u.owner_id = a.owner_id
            join media.media_variants v on v.media_asset_id = a.id and v.kind = {kind}
            where a.id = {assetId} and a.owner_id = {owner.UserId}
              and a.state = 'Approved' and a.deleted_at is null and u.state = 'Approved'
              and not exists (select 1 from media.photo_tombstones t where t.photo_id = u.id)
            """).SingleOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return stored is null ? null : ObjectKey.Parse(stored);
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
}
