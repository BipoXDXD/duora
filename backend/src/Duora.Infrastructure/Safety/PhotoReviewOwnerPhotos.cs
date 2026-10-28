using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

public readonly record struct ReviewPhotoCursor(DateTimeOffset CreatedAt, Guid PhotoId);
public sealed record ReviewPhoto(Guid PhotoId, string Status, string? RejectionCode, DateTimeOffset CreatedAt);
public sealed record ReviewPhotoPage(IReadOnlyList<ReviewPhoto> Items, ReviewPhotoCursor? NextCursor);

public sealed partial class PhotoReviewService
{
    public async Task<ReviewReply<ReviewPhotoPage>> OwnerPhotosAsync(Guid user, Guid session, ReviewPhotoCursor? cursor,
        int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 50);
        if (cursor is { PhotoId: var photo } && photo == Guid.Empty)
        {
            throw new ArgumentException("A photo cursor requires a nonempty identifier.", nameof(cursor));
        }
        await using var db = await _contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlRawAsync("set local statement_timeout='5s'", ct);
        var actor = new PhotoReviewActor(user, session);
        if (!await PhotoOwnerAccess.AuthorizeAsync(db, _time, actor, ct))
        {
            return new(null, ReviewRefusal.Forbidden);
        }
        var hasCursor = cursor.HasValue;
        var createdAt = cursor?.CreatedAt ?? DateTimeOffset.MinValue;
        var photoId = cursor?.PhotoId ?? Guid.Empty;
        // The owner predicate is unconditional, including every continuation. No global lookup or count exists.
        var rows = await db.Database.SqlQuery<ReviewPhoto>($"""
            select u.id as "PhotoId",u.state as "Status",u.created_at as "CreatedAt",
              case when u.state='Rejected' then u.rejection_code else null end as "RejectionCode"
            from media.uploads u where u.owner_id={user}
              and u.state in ('Pending','Quarantined','Processing','AwaitingReview','Approved','Rejected')
              and (not {hasCursor} or (u.created_at,u.id)>({createdAt},{photoId}))
            order by u.created_at,u.id limit {limit + 1}
            """).ToListAsync(ct);
        if (!await PhotoOwnerAccess.AuthorizeAsync(db, _time, actor, ct))
        {
            return new(null, ReviewRefusal.Forbidden);
        }
        var items = rows.Take(limit).ToArray();
        ReviewPhotoCursor? next = rows.Count > limit ? new(items[^1].CreatedAt, items[^1].PhotoId) : null;
        await transaction.CommitAsync(ct);
        return new(new(items, next), null);
    }
}
