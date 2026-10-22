using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Media;

/// <summary>Durable obligation committed with the upload outcome, never an enumeration of storage files.</summary>
internal static class OriginalCleanupQueue
{
    internal static async Task EnqueueAsync(
        DuoraDbContext db, Guid uploadId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Original cleanup must be recorded in the processing transaction.");
        }

        var changed = await db.Database.ExecuteSqlAsync($"""
            insert into media.original_cleanup (upload_id, object_key, created_at, available_at)
            select id, quarantine_key, {now}, {now} from media.uploads
            where id = {uploadId} and state in ('Approved', 'Rejected', 'Deleted', 'AwaitingReview')
            on conflict (upload_id) do update set object_key = original_cleanup.object_key
            where original_cleanup.object_key = excluded.object_key
            """, cancellationToken);
        if (changed != 1)
        {
            throw new InvalidOperationException("Cleanup requires a decided upload with its immutable original key.");
        }
    }

    /// <summary>A producer cannot acknowledge work already owned by a background consumer.</summary>
    internal static Task<int> CompleteImmediateAsync(
        DuoraDbContext db, Guid uploadId, ObjectKey key, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"""
            delete from media.original_cleanup
            where upload_id = {uploadId} and object_key = {key.Value} and lease_until is null
            """, cancellationToken);
}
