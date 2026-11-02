using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.Infrastructure.Media;

internal sealed record PrivateGenerationBinding(Guid Domain, long Epoch, int Layout, string ManifestHash)
{
    internal PrivateGenerationIdentity Identity(PrivateGenerationArea area, Guid generation) =>
        new(new(Domain, Epoch, Layout), area, generation, ManifestHash);
}

internal static class PrivateGenerationMetadata
{
    /// <summary>Caller owns the same transaction that inserted this generation and its variants.</summary>
    internal static async Task BindAsync(DuoraDbContext db, IObjectStore store, PrivateGenerationArea area,
        Guid generation, IEnumerable<PrivateGenerationVariant> variants, CancellationToken cancellationToken)
    {
        if (store is not IPrivateGenerationStore managed)
        {
            return;
        }
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Private binding must precede the preparation commit.");
        }
        var manifest = PrivateGenerationManifest.Create(managed.Domain, area, generation, variants);
        await db.Database.ExecuteSqlAsync($"""
            insert into media.private_generation_bindings(generation,area,domain,epoch,layout,manifest_hash)
            values({generation},{(int)area},{managed.Domain.Id},{managed.Domain.Epoch},
              {managed.Domain.Layout},{manifest.Identity.ManifestHash})
            """, cancellationToken);
    }

    internal static PrivateGenerationManifest? Manifest(IObjectStore store, PrivateGenerationArea area,
        Guid generation, IEnumerable<PrivateGenerationVariant> variants) =>
        store is IPrivateGenerationStore managed
            ? PrivateGenerationManifest.Create(managed.Domain, area, generation, variants) : null;

    internal static async Task<PrivateGenerationBinding?> FindAsync(
        NpgsqlDataSource source, PrivateGenerationArea area, Guid generation, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            select domain,epoch,layout,manifest_hash from media.private_generation_bindings
            where generation=$1 and area=$2
            """);
        command.Parameters.AddWithValue(generation);
        command.Parameters.AddWithValue((int)area);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)) : null;
    }

    /// <summary>A fresh complete sweep follows Close; never upgrades an already-started uncertain DELETE pass.</summary>
    internal static async Task<Guid?> CloseAndSweepAsync(NpgsqlDataSource source, IObjectStore store,
        PrivateGenerationArea area, Guid generation, Guid token, CancellationToken cancellationToken)
    {
        var binding = await FindAsync(source, area, generation, cancellationToken);
        if (binding is null)
        {
            return null;
        }
        if (store is not IPrivateGenerationStore managed || managed.Domain.Id != binding.Domain
            || managed.Domain.Layout != binding.Layout || managed.Domain.Epoch < binding.Epoch)
        {
            throw new InvalidOperationException("Managed storage binding has no admitted authority.");
        }
        var closed = await managed.CloseAsync(binding.Identity(area, generation), cancellationToken);
        return await managed.SweepAsync(closed, token, cancellationToken);
    }
}

internal sealed record PrivateGenerationStorageRecord(PrivateGenerationManifest Manifest, bool CanWrite, bool CanClose,
    bool CanDelete);

internal sealed class PrivateGenerationAuthorityMetadata(NpgsqlDataSource source)
{
    internal async Task<PrivateGenerationStorageRecord?> ReadAsync(PrivateGenerationArea area, Guid generation,
        CancellationToken cancellationToken)
    {
        var (table, variants) = area switch
        {
            PrivateGenerationArea.Candidate => ("review_candidates", "review_candidate_variants"),
            PrivateGenerationArea.Promotion => ("review_promotions", "review_promotion_variants"),
            _ => throw new InvalidDataException("Invalid storage area."),
        };
        await using var command = source.CreateCommand($"""
            select b.domain,b.epoch,b.layout,b.manifest_hash,
              (c.committed_at is null and c.cleanup_requested_at is null and c.deleted_at is null
                and c.prepare_deadline>statement_timestamp()) as can_write,
              (c.deleted_at is not null or (c.cleanup_requested_at is not null and c.cleanup_requested_at<=statement_timestamp()
                  and c.cleanup_available_at<=statement_timestamp())
                or (c.committed_at is null and c.prepare_deadline<=statement_timestamp())
                or u.state='Deleted') as can_close,
              (u.state='Deleted' and exists(select 1 from media.photo_deletion_cleanup d where d.photo_id=u.id))
                as can_delete,
              v.kind,v.size_bytes,v.sha256
            from media.private_generation_bindings b
            join media.{table} c on c.generation=b.generation join media.uploads u on u.id=c.upload_id
            join media.{variants} v on v.generation=c.generation
            where b.generation=$1 and b.area=$2 order by v.kind
            """);
        command.Parameters.AddWithValue(generation);
        command.Parameters.AddWithValue((int)area);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        PrivateGenerationIdentity? identity = null;
        var values = new List<PrivateGenerationVariant>();
        var canWrite = false;
        var canClose = false;
        var canDelete = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            identity ??= new(new(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt32(2)), area, generation,
                reader.GetString(3));
            canWrite = reader.GetBoolean(4);
            canClose = reader.GetBoolean(5);
            canDelete = reader.GetBoolean(6);
            var name = reader.GetString(7);
            var kind = PhotoVariants.All.Single(k => PhotoVariants.Name(k) == name);
            values.Add(new(kind, reader.GetInt64(8), Convert.ToHexStringLower((byte[])reader[9])));
        }
        if (identity is null)
        {
            return null;
        }
        var manifest = new PrivateGenerationManifest(identity, [.. values]);
        manifest.Validate();
        return new(manifest, canWrite, canClose, canDelete);
    }

    internal async Task<Guid> RecordSweepAsync(PrivateGenerationCloseReceipt receipt, Guid token,
        CancellationToken cancellationToken)
    {
        var id = receipt.Identity;
        await using var command = source.CreateCommand("""
            select media.record_private_generation_sweep($1,$2,$3,$4,$5,$6,$7,$8,$9)
            """);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(id.Generation);
        command.Parameters.AddWithValue((int)id.Area);
        command.Parameters.AddWithValue(id.Domain.Id);
        command.Parameters.AddWithValue(id.Domain.Epoch);
        command.Parameters.AddWithValue(id.Domain.Layout);
        command.Parameters.AddWithValue(id.ManifestHash);
        command.Parameters.AddWithValue(token);
        command.Parameters.AddWithValue(receipt.Revision);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid proof
            ? proof : throw new InvalidDataException("Storage proof was not persisted.");
    }
}
