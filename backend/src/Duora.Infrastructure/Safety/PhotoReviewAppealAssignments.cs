using System.Data;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

internal abstract record PhotoReviewAppealAssignmentResult
{
    internal sealed record Assigned(Guid AppealId, long Version) : PhotoReviewAppealAssignmentResult;
    internal sealed record Forbidden : PhotoReviewAppealAssignmentResult;
    internal sealed record NotFound : PhotoReviewAppealAssignmentResult;
    internal sealed record PreconditionFailed : PhotoReviewAppealAssignmentResult;
    internal sealed record KeyReused : PhotoReviewAppealAssignmentResult;
    internal sealed record ReviewerUnavailable : PhotoReviewAppealAssignmentResult;
    internal sealed record StateConflict : PhotoReviewAppealAssignmentResult;
}

/// <summary>Audited appeal routing only; never grants access or registers a real reviewer.</summary>
internal sealed class PhotoReviewAppealAssignments
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;
    private readonly TimeProvider _time;

    internal PhotoReviewAppealAssignments(IDbContextFactory<DuoraDbContext> contexts, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(time);
        _contexts = contexts;
        _time = time;
    }

    internal async Task<PhotoReviewAppealAssignmentResult> AssignAsync(PhotoReviewActor actor, Guid appealId,
        Guid reviewerId, long expectedVersion, IdempotencyKey key, CancellationToken cancellationToken)
    {
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Appeal assignments own their complete READ COMMITTED transaction.");
        }
        if (!IdempotencyKey.TryParse(key.Value, out _))
        {
            throw new ArgumentException("A validated idempotency key is required.", nameof(key));
        }

        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value));
        var intent = SHA256.HashData(Encoding.UTF8.GetBytes($"assign-photo-appeal\n{appealId:D}\n{reviewerId:D}"));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
            var access = new PhotoReviewAccess(db, _time);
            // Release every actor lock before discovering and ordering another user's guard.
            await using (var preauthorization = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Assign, cancellationToken))
                {
                    return new PhotoReviewAppealAssignmentResult.Forbidden();
                }
                if (await ReplayAsync(db, actor.UserId, keyHash, intent, cancellationToken) is { } confirmed)
                {
                    return confirmed;
                }
            }

            var discovered = await db.Database.SqlQuery<Discovered>($"""
                select u.owner_id as "OwnerId",u.id as "UploadId",r.id as "CaseId",r.candidate_generation as "Generation"
                from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
                join media.uploads u on u.id=r.upload_id where a.id={appealId}
                """).SingleOrDefaultAsync(cancellationToken);
            if (discovered is null)
            {
                return new PhotoReviewAppealAssignmentResult.NotFound();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var present = await GuardUsersAsync(db, [actor.UserId, discovered.OwnerId, reviewerId], cancellationToken);
            if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Assign, cancellationToken))
            {
                return new PhotoReviewAppealAssignmentResult.Forbidden();
            }
            if (await ReplayAsync(db, actor.UserId, keyHash, intent, cancellationToken) is { } replay)
            {
                return replay;
            }
            if (!present)
            {
                return new PhotoReviewAppealAssignmentResult.ReviewerUnavailable();
            }

            await LockBusinessRowsAsync(db, appealId, discovered, cancellationToken);
            if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Assign, cancellationToken))
            {
                return new PhotoReviewAppealAssignmentResult.Forbidden();
            }
            var state = await db.Database.SqlQuery<AppealState>($"""
                select a.version as "Version",d.actor_id as "OriginalActorId"
                from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
                join media.uploads u on u.id=r.upload_id
                join safety.photo_review_decisions d on d.id=a.original_decision_id
                where a.id={appealId} and r.id={discovered.CaseId} and u.id={discovered.UploadId}
                  and u.owner_id={discovered.OwnerId} and r.candidate_generation={discovered.Generation}
                """).SingleOrDefaultAsync(cancellationToken);
            if (state is null)
            {
                continue; // Discovery changed: release ALL locks before discovering and ordering again.
            }
            if (!await IsLiveAsync(db, appealId, discovered, _time.GetUtcNow(), cancellationToken)
                || state.Version == long.MaxValue)
            {
                return new PhotoReviewAppealAssignmentResult.StateConflict();
            }
            if (reviewerId == state.OriginalActorId || !await ReviewerReadyAsync(db, reviewerId, cancellationToken))
            {
                return new PhotoReviewAppealAssignmentResult.ReviewerUnavailable();
            }
            if (state.Version != expectedVersion)
            {
                return new PhotoReviewAppealAssignmentResult.PreconditionFailed();
            }

            // Recheck live authority and time after metadata reads, immediately before the audited effect.
            if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Assign, cancellationToken))
            {
                return new PhotoReviewAppealAssignmentResult.Forbidden();
            }
            var now = _time.GetUtcNow();
            if (!await IsLiveAsync(db, appealId, discovered, now, cancellationToken))
            {
                return new PhotoReviewAppealAssignmentResult.StateConflict();
            }
            var version = checked(state.Version + 1);
            var assignmentId = Guid.CreateVersion7(now);
            var changed = await db.Database.ExecuteSqlAsync($"""
                update safety.photo_review_appeals set version={version}
                where id={appealId} and status='Open' and closed_at is null and version={state.Version}
                """, cancellationToken);
            if (changed != 1)
            {
                throw new InvalidOperationException("The guarded appeal version must advance exactly once.");
            }
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.photo_review_appeal_assignments(id,appeal_id,appeal_version,reviewer_id,assigned_by,assigned_at)
                values({assignmentId},{appealId},{version},{reviewerId},{actor.UserId},{now})
                """, cancellationToken);
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.photo_review_appeal_assignment_commands(actor_id,key_hash,intent_sha256,appeal_id,result_version,created_at)
                values({actor.UserId},{keyHash},{intent},{appealId},{version},{now})
                """, cancellationToken);
            await new AuditTrail(db, _time).AppendAsync(new AuditRecord(AuditActorKind.Moderator, actor.UserId,
                AuditAction.PhotoReviewAppealAssigned, AuditTargetTypes.PhotoReviewAppeal, appealId, targetVersion: version), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PhotoReviewAppealAssignmentResult.Assigned(appealId, version);
        }
        return new PhotoReviewAppealAssignmentResult.StateConflict();
    }

    private static async Task<PhotoReviewAppealAssignmentResult?> ReplayAsync(DuoraDbContext db, Guid actorId,
        byte[] keyHash, byte[] intent, CancellationToken cancellationToken)
    {
        var receipt = await db.Database.SqlQuery<Receipt>($"""
            select appeal_id as "AppealId",intent_sha256 as "IntentSha256",result_version as "ResultVersion"
            from safety.photo_review_appeal_assignment_commands where actor_id={actorId} and key_hash={keyHash}
            """).SingleOrDefaultAsync(cancellationToken);
        return receipt is null ? null
            : CryptographicOperations.FixedTimeEquals(receipt.IntentSha256, intent)
                ? new PhotoReviewAppealAssignmentResult.Assigned(receipt.AppealId, receipt.ResultVersion)
                : new PhotoReviewAppealAssignmentResult.KeyReused();
    }

    private static async Task<bool> GuardUsersAsync(DuoraDbContext db, Guid[] users, CancellationToken cancellationToken)
    {
        var ordered = users.Distinct().Order().ToArray();
        var present = true;
        await db.Database.ExecuteSqlAsync($"set local lock_timeout='2s'", cancellationToken);
        foreach (var user in ordered)
        {
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.user_guards(user_id) select id from identity.users where id={user} on conflict do nothing
                """, cancellationToken);
            var guards = await db.Database.SqlQuery<Guid>($"""
                select user_id as "Value" from safety.user_guards where user_id={user} for update
                """).ToListAsync(cancellationToken);
            present &= guards.Count == 1;
        }
        foreach (var user in ordered)
        {
            await db.Database.ExecuteSqlAsync($"select 1 from identity.users where id={user} for share", cancellationToken);
            await db.Database.ExecuteSqlAsync($"""
                select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
                where ur.user_id={user} order by ur.role_id for share of ur,r nowait
                """, cancellationToken);
        }
        return present;
    }

    private static async Task LockBusinessRowsAsync(DuoraDbContext db, Guid appealId,
        Discovered discovered, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={discovered.UploadId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={discovered.CaseId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            select 1 from media.review_candidates where generation={discovered.Generation} for share
            """, cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_appeals where id={appealId} for update", cancellationToken);
    }

    private static Task<bool> ReviewerReadyAsync(DuoraDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"""
            select exists(select 1 from identity.users u where u.id={userId}
              and u.status='Active' and u.email_confirmed and u.two_factor_enabled and u.temporary_password_expires_at is null
              and exists(select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
                where ur.user_id=u.id and r.name='moderator')
              and coalesce((select enabled from safety.photo_review_grants where user_id=u.id and capability='evidence'
                order by version desc limit 1),false)
              and coalesce((select enabled from safety.photo_review_grants where user_id=u.id and capability='appeal'
                order by version desc limit 1),false)) as "Value"
            """).SingleAsync(cancellationToken);

    private static Task<bool> IsLiveAsync(DuoraDbContext db, Guid appealId, Discovered discovered,
        DateTimeOffset now, CancellationToken cancellationToken) => db.Database.SqlQuery<bool>($"""
            select exists(select 1 from safety.photo_review_appeals a
              join safety.photo_review_cases r on r.id=a.case_id join media.uploads u on u.id=r.upload_id
              join identity.users owner on owner.id=u.owner_id
              join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
              join safety.photo_review_decisions d on d.id=a.original_decision_id and d.case_id=r.id
                and d.candidate_generation=c.generation and d.policy_version=r.policy_version
              where a.id={appealId} and r.id={discovered.CaseId} and u.id={discovered.UploadId}
                and u.owner_id={discovered.OwnerId} and r.candidate_generation={discovered.Generation}
                and a.owner_id=u.owner_id and a.candidate_generation=c.generation and a.policy_version=r.policy_version
                and a.status='Open' and a.closed_at is null and a.version>=1
                and r.policy_version='human-review-v1' and d.appeal_id is null
                and d.decision='reject' and d.reason_code='does_not_meet_policy'
                and r.version>0 and d.case_version=r.version-1
                and a.original_rejected_at=d.decided_at
                and d.candidate_retention_until_snapshot=d.decided_at+interval '168 hours'
                and a.requested_at>=d.decided_at and a.requested_at<d.decided_at+interval '168 hours'
                and a.response_target_at=a.requested_at+interval '168 hours'
                and a.maximum_retention_until=d.decided_at+interval '720 hours'
                and {now}>=a.requested_at and {now}<a.maximum_retention_until
                and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_REJECTED'
                and u.version>=3 and c.upload_version=u.version-3
                and owner.email_confirmed and owner.status in ('PendingEligibility','Active','Restricted','Suspended')
                and c.committed_at is not null and c.writer_settled_at is not null
                and c.retention_until=a.maximum_retention_until
                and c.cleanup_requested_at is null and c.cleanup_audit_id is null and c.deleted_at is null
                and c.cleanup_token is null and c.cleanup_lease_until is null) as "Value"
            """).SingleAsync(cancellationToken);

    private sealed record Discovered(Guid OwnerId, Guid UploadId, Guid CaseId, Guid Generation);
    private sealed record AppealState(long Version, Guid OriginalActorId);
    private sealed record Receipt(Guid AppealId, byte[] IntentSha256, long ResultVersion);
}
