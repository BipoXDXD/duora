using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

/// <summary>A system deadline, never a human decision. Not registered or exposed before the final composition.</summary>
internal sealed class PhotoReviewExpirations(IDbContextFactory<DuoraDbContext> contexts, TimeProvider time)
{
    private const string PolicyVersion = "human-review-v1";

    internal async Task<int> ExpireOnceAsync(CancellationToken cancellationToken)
    {
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Review expiration owns its short transaction.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var observedAt = time.GetUtcNow();
        var discovered = await db.Database.SqlQuery<Discovered>($"""
            select r.id as "CaseId",u.id as "UploadId",u.owner_id as "OwnerId",
              r.candidate_generation as "Generation",u.version as "UploadVersion",r.version as "CaseVersion"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            where u.state='AwaitingReview' and r.policy_version={PolicyVersion} and r.review_deadline<={observedAt}
              and u.version<9223372036854775807 and r.version<9223372036854775807
              and c.upload_version=u.version-1 and c.retention_until=r.review_deadline
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.cleanup_audit_id is null and c.deleted_at is null
            order by r.review_deadline,r.id limit 1
            """).SingleOrDefaultAsync(cancellationToken);
        if (discovered is null)
        {
            return 0;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await db.Database.ExecuteSqlAsync($"set local lock_timeout='2s'", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.user_guards(user_id)
            select id from identity.users where id={discovered.OwnerId} on conflict do nothing
            """, cancellationToken);
        var guards = await db.Database.SqlQuery<Guid>($"""
            select user_id as "Value" from safety.user_guards where user_id={discovered.OwnerId} for update
            """).ToListAsync(cancellationToken);
        if (guards.Count != 1)
        {
            return 0;
        }

        await db.Database.ExecuteSqlAsync($"select 1 from identity.users where id={discovered.OwnerId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={discovered.UploadId} for update", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={discovered.CaseId} for update", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={discovered.Generation} for update", cancellationToken);

        // The discovery is not authority: state, generation, versions and the clock are read after all waits.
        var now = time.GetUtcNow();
        var current = await db.Database.SqlQuery<Guarded>($"""
            select u.version as "UploadVersion",r.version as "CaseVersion"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            where r.id={discovered.CaseId} and u.id={discovered.UploadId} and u.owner_id={discovered.OwnerId}
              and r.candidate_generation={discovered.Generation} and r.policy_version={PolicyVersion}
              and u.state='AwaitingReview' and u.version={discovered.UploadVersion} and r.version={discovered.CaseVersion}
              and u.version<9223372036854775807 and r.version<9223372036854775807
              and c.upload_version=u.version-1 and r.review_deadline<={now} and c.retention_until=r.review_deadline
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.cleanup_audit_id is null and c.deleted_at is null
            """).SingleOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return 0;
        }

        var photoVersion = checked(current.UploadVersion + 1);
        var caseVersion = checked(current.CaseVersion + 1);
        var changedUpload = await db.Database.ExecuteSqlAsync($"""
            update media.uploads set state='Rejected',rejection_code={PhotoReviewResults.Expired},version={photoVersion}
            where id={discovered.UploadId} and state='AwaitingReview' and version={current.UploadVersion}
            """, cancellationToken);
        var changedCase = await db.Database.ExecuteSqlAsync($"""
            update safety.photo_review_cases set version={caseVersion}
            where id={discovered.CaseId} and version={current.CaseVersion} and candidate_generation={discovered.Generation}
            """, cancellationToken);
        await new AuditTrail(db, time).AppendAsync(new AuditRecord(AuditActorKind.System, null,
            AuditAction.PhotoReviewExpired, AuditTargetTypes.PhotoReviewCase, discovered.CaseId, caseVersion), cancellationToken);
        // A monotonic case version under its guard identifies exactly this committed fact; never infer MAX(id).
        var auditId = await db.Database.SqlQuery<long>($"""
            select id as "Value" from ops.audit_events where actor_kind='System' and actor_id is null
              and action={AuditAction.PhotoReviewExpired.Code} and target_type={AuditTargetTypes.PhotoReviewCase}
              and target_id={discovered.CaseId} and target_version={caseVersion}
            """).SingleAsync(cancellationToken);
        if (auditId <= 0)
        {
            throw new InvalidOperationException("Review expiration requires one real audit identity.");
        }

        var changedCandidate = await db.Database.ExecuteSqlAsync($"""
            update media.review_candidates set cleanup_requested_at={now},cleanup_available_at=least(cleanup_available_at,{now}),
              cleanup_audit_id={auditId}
            where generation={discovered.Generation} and case_id={discovered.CaseId} and upload_id={discovered.UploadId}
              and committed_at is not null and writer_settled_at is not null
              and cleanup_requested_at is null and cleanup_audit_id is null and deleted_at is null
            """, cancellationToken);
        if (changedUpload != 1 || changedCase != 1 || changedCandidate != 1)
        {
            throw new InvalidOperationException("All guarded expiration projections must change exactly once.");
        }

        // A lost COMMIT response propagates failure. A later invocation observes the persisted final state.
        await transaction.CommitAsync(cancellationToken);
        return 1;
    }

    private sealed record Discovered(Guid CaseId, Guid UploadId, Guid OwnerId, Guid Generation, long UploadVersion, long CaseVersion);

    private sealed record Guarded(long UploadVersion, long CaseVersion);
}
