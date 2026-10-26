using System.Data;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

public sealed partial class PhotoReviewService
{
    public async Task<ReviewReply<ReviewHistory>> HistoryAsync(Guid user, Guid session, Guid photo, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var owner = new PhotoReviewActor(user, session);
        var access = new PhotoReviewAppeals(_contexts, _store, _time);
        if (!await access.AuthorizeOwnerAsync(db, owner, ct))
        {
            return new(null, ReviewRefusal.Forbidden);
        }
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={photo} and owner_id={user} for share", ct);
        var row = await db.Database.SqlQuery<HistoryPhoto>($"""
            select u.version as "Version",u.state as "State",u.rejection_code as "RejectionCode",r.id as "CaseId"
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
        var items = await db.Database.SqlQuery<ReviewHistoryItem>($"""
            select d.id as "DecisionId",d.decision as "Decision",d.reason_code as "ReasonCode",d.decided_at as "DecidedAt",
              (d.appeal_id is not null) as "IsAppeal"
            from safety.photo_review_decisions d join safety.photo_review_cases r on r.id=d.case_id
            where r.upload_id={photo} order by d.case_version limit 3
            """).ToListAsync(ct);
        if (items.Count > 2 || items.Count(i => !i.IsAppeal) > 1 || items.Count(i => i.IsAppeal) > 1)
        {
            return new(null, ReviewRefusal.Unavailable);
        }
        var appeal = await AppealQuery(db, photo, true).SingleOrDefaultAsync(ct);
        if (!await access.AuthorizeOwnerAsync(db, owner, ct))
        {
            return new(null, ReviewRefusal.Forbidden);
        }
        var now = _time.GetUtcNow();
        var origin = items.SingleOrDefault(i => !i.IsAppeal);
        var canRequest = appeal is null && row.State == "Rejected" && row.RejectionCode == "PHOTO_REVIEW_REJECTED"
            && origin is { Decision: "reject", ReasonCode: "does_not_meet_policy" }
            && now >= origin.DecidedAt && now < origin.DecidedAt.AddDays(7);
        await transaction.CommitAsync(ct);
        return new(new(photo, row.Version, items, appeal, canRequest), null);
    }

    public async Task<ReviewReply<ReviewAppealPage>> AppealsAsync(Guid user, Guid session, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 50);
        var parsed = ParseCursor(cursor);
        await using var db = await _contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await AuthorizeAppealReadAsync(db, new(user, session), ct))
        {
            return new(null, ReviewRefusal.Forbidden);
        }
        var now = _time.GetUtcNow();
        var at = parsed?.At ?? DateTimeOffset.MinValue;
        var id = parsed?.Id ?? Guid.Empty;
        var hasCursor = parsed.HasValue;
        var rows = await db.Database.SqlQuery<ReviewAppeal>($"""
            select a.id as "AppealId",u.id as "PhotoId",a.status as "State",a.version as "Version",
              a.requested_at as "RequestedAt",a.response_target_at as "ResponseTargetAt",
              a.maximum_retention_until as "MaximumRetentionUntil",a.closed_at as "ClosedAt"
            from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id join identity.users owner on owner.id=u.owner_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            join safety.photo_review_decisions d on d.id=a.original_decision_id and d.case_id=r.id
              and d.candidate_generation=c.generation and d.policy_version=r.policy_version
            join safety.photo_review_appeal_assignments s on s.appeal_id=a.id and s.appeal_version=a.version
            where a.status='Open' and a.closed_at is null and s.reviewer_id={user} and d.actor_id<>{user}
              and d.appeal_id is null and d.decision='reject' and d.reason_code='does_not_meet_policy'
              and d.case_version=r.version-1 and a.original_rejected_at=d.decided_at
              and a.owner_id=u.owner_id and a.candidate_generation=c.generation and a.policy_version=r.policy_version
              and r.policy_version='human-review-v1' and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_REJECTED'
              and c.upload_version=u.version-3 and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.cleanup_audit_id is null and c.deleted_at is null
              and c.cleanup_token is null and c.cleanup_lease_until is null
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active','Restricted','Suspended')
              and a.maximum_retention_until=d.decided_at+interval '720 hours' and c.retention_until=a.maximum_retention_until
              and {now}>=a.requested_at and {now}<a.maximum_retention_until
              and (not {hasCursor} or (a.requested_at,a.id)>({at},{id}))
            order by a.requested_at,a.id limit {limit + 1}
            """).ToListAsync(ct);
        var items = rows.Take(limit).ToArray();
        return new(new(items, rows.Count > limit ? Cursor(items[^1].RequestedAt, items[^1].AppealId) : null), null);
    }

    public async Task<ReviewReply<ReviewAppeal>> AppealAsync(Guid user, Guid session, Guid id, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await AuthorizeAppealReadAsync(db, new(user, session), ct))
        {
            return new(null, ReviewRefusal.Forbidden);
        }
        // Metadata only. Evidence and commands independently enforce the full original/current candidate proof.
        var row = await db.Database.SqlQuery<ReviewAppeal>($"""
            select a.id as "AppealId",u.id as "PhotoId",a.status as "State",a.version as "Version",
              a.requested_at as "RequestedAt",a.response_target_at as "ResponseTargetAt",
              a.maximum_retention_until as "MaximumRetentionUntil",a.closed_at as "ClosedAt"
            from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id join identity.users owner on owner.id=u.owner_id
            join safety.photo_review_decisions d on d.id=a.original_decision_id and d.case_id=r.id
            where a.id={id} and u.state<>'Deleted' and a.owner_id=u.owner_id
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active','Restricted','Suspended')
              and a.candidate_generation=r.candidate_generation and a.policy_version=r.policy_version
              and d.appeal_id is null and d.actor_id<>{user} and d.decision='reject'
              and (select s.reviewer_id from safety.photo_review_appeal_assignments s where s.appeal_id=a.id
                order by s.appeal_version desc limit 1)={user}
            """).SingleOrDefaultAsync(ct);
        return row is null ? new(null, ReviewRefusal.NotFound) : new(row, null);
    }

    private async Task<bool> AuthorizeAppealReadAsync(DuoraDbContext db, PhotoReviewActor actor, CancellationToken ct) =>
        await new PhotoReviewAccess(db, _time).AuthorizeAsync(actor, PhotoReviewCapability.Evidence, ct)
        && await HasGrantAsync(db, actor.UserId, "appeal", ct);

    private static IQueryable<ReviewAppeal> AppealQuery(DuoraDbContext db, Guid id, bool byPhoto) =>
        db.Database.SqlQuery<ReviewAppeal>($"""
            select a.id as "AppealId",u.id as "PhotoId",a.status as "State",a.version as "Version",
              a.requested_at as "RequestedAt",a.response_target_at as "ResponseTargetAt",
              a.maximum_retention_until as "MaximumRetentionUntil",a.closed_at as "ClosedAt"
            from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id
            join safety.photo_review_decisions d on d.id=a.original_decision_id and d.case_id=r.id
            where a.owner_id=u.owner_id and a.candidate_generation=r.candidate_generation and a.policy_version=r.policy_version
              and d.appeal_id is null and d.decision='reject' and d.candidate_generation=r.candidate_generation
              and d.policy_version=r.policy_version and a.original_rejected_at=d.decided_at
              and a.maximum_retention_until=d.decided_at+interval '720 hours'
              and (({byPhoto} and u.id={id}) or (not {byPhoto} and a.id={id}))
            """);

    private async Task<Guid> PhotoIdAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        return await db.Database.SqlQuery<Guid>($"select upload_id as \"Value\" from safety.photo_review_cases where id={caseId}")
            .SingleAsync(ct);
    }

    private sealed record HistoryPhoto(long Version, string State, string? RejectionCode, Guid? CaseId);
}
