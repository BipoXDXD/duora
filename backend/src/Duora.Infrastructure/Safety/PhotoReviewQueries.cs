using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

public sealed partial class PhotoReviewService
{
    public async Task<ReviewReply<ReviewHistory>> HistoryAsync(Guid user, Guid session, Guid photo, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var owner = new PhotoReviewActor(user, session);
        if (!await PhotoOwnerAccess.AuthorizeAsync(db, _time, owner, ct))
        {
            return new(null, ReviewRefusal.Forbidden);
        }
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={photo} and owner_id={user} for share", ct);
        var row = await db.Database.SqlQuery<HistoryPhoto>($"""
            select u.version as "Version",r.id as "CaseId"
            from media.uploads u left join safety.photo_review_cases r on r.upload_id=u.id
            where u.id={photo} and u.owner_id={user} and u.state<>'Deleted'
            """).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return new(null, ReviewRefusal.NotFound);
        }
        if (row.CaseId is { } caseId)
        {
            await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={caseId} for share", ct);
        }
        // One review per upload: a rejected owner sends a new photo, which opens a new case (no appeal exists).
        var items = await db.Database.SqlQuery<ReviewHistoryItem>($"""
            select d.id as "DecisionId",d.decision as "Decision",d.reason_code as "ReasonCode",d.decided_at as "DecidedAt"
            from safety.photo_review_decisions d join safety.photo_review_cases r on r.id=d.case_id
            where r.upload_id={photo} order by d.case_version limit 2
            """).ToListAsync(ct);
        if (items.Count > 1)
        {
            return new(null, ReviewRefusal.Unavailable);
        }
        if (!await PhotoOwnerAccess.AuthorizeAsync(db, _time, owner, ct))
        {
            return new(null, ReviewRefusal.Forbidden);
        }
        await transaction.CommitAsync(ct);
        return new(new(photo, row.Version, items), null);
    }

    private async Task<Guid> PhotoIdAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        return await db.Database.SqlQuery<Guid>($"select upload_id as \"Value\" from safety.photo_review_cases where id={caseId}")
            .SingleAsync(ct);
    }

    private sealed record HistoryPhoto(long Version, Guid? CaseId);
}
