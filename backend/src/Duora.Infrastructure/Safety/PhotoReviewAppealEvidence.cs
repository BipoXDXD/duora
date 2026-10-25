using System.Data;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

/// <summary>Internal private evidence service; all contexts end before storage or consumer pauses.</summary>
internal sealed class PhotoReviewAppealEvidence
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;

    internal PhotoReviewAppealEvidence(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _contexts = contexts;
        _store = store;
        _time = time;
    }

    internal async Task<PhotoReviewEvidenceResult> OpenAsync(
        PhotoReviewActor actor, Guid appealId, PhotoVariant variant, CancellationToken cancellationToken)
    {
        _ = PhotoVariants.Name(variant);
        RequireNoAmbientTransaction();
        var initial = await GuardedAsync(actor, appealId, variant, expected: null, audit: false, cancellationToken);
        if (initial.Snapshot is not { } snapshot)
        {
            return Denied(initial);
        }
        var buffer = await ReadPrivateBytesAsync(snapshot, variant, cancellationToken);
        if (buffer is null)
        {
            return new PhotoReviewEvidenceResult.Unavailable();
        }
        try
        {
            var final = await GuardedAsync(actor, appealId, variant, snapshot, audit: true, cancellationToken);
            if (final.Snapshot is null)
            {
                return Denied(final);
            }
            var lease = new PhotoReviewEvidenceLease(buffer, async ct =>
                (await GuardedAsync(actor, appealId, variant, snapshot, audit: false, ct)).Snapshot is not null);
            buffer = null; // Ownership transfers only after access record, audit and COMMIT are confirmed.
            return new PhotoReviewEvidenceResult.Opened(lease);
        }
        finally
        {
            if (buffer is not null)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private async Task<byte[]?> ReadPrivateBytesAsync(Snapshot snapshot, PhotoVariant variant, CancellationToken cancellationToken)
    {
        if (snapshot.SizeBytes is < 1 or > 16_777_216 || snapshot.Sha256.Length != 32)
        {
            return null;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        byte[]? buffer = null;
        var excess = new byte[1];
        var confirmed = false;
        try
        {
            await using (var stream = await _store.OpenReadAsync(ObjectKey.ForReviewCandidate(snapshot.Generation, variant), budget.Token))
            {
                if (stream is null)
                {
                    return null;
                }
                buffer = new byte[checked((int)snapshot.SizeBytes)];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(offset), budget.Token);
                    if (read == 0)
                    {
                        return null;
                    }
                    offset += read;
                }
                if (await stream.ReadAsync(excess, budget.Token) != 0
                    || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(buffer), snapshot.Sha256))
                {
                    return null;
                }
            }
            // Disposal is part of the observed storage operation; a disposal failure must also clear bytes.
            budget.Token.ThrowIfCancellationRequested();
            confirmed = true;
            return buffer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(excess);
            if (!confirmed && buffer is not null)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private static PhotoReviewEvidenceResult Denied(Guarded result) => result.Forbidden
        ? new PhotoReviewEvidenceResult.Forbidden() : result.Unavailable
            ? new PhotoReviewEvidenceResult.Unavailable() : new PhotoReviewEvidenceResult.NotFound();

    private static void RequireNoAmbientTransaction()
    {
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Evidence owns its complete READ COMMITTED phases.");
        }
    }

    private async Task<Guarded> GuardedAsync(PhotoReviewActor actor, Guid appealId, PhotoVariant variant,
        Snapshot? expected, bool audit, CancellationToken cancellationToken)
    {
        RequireNoAmbientTransaction();
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var access = new PhotoReviewAccess(db, _time);
        await using (var preauthorization = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            if (!await AuthorizeReadAsync(db, access, actor, cancellationToken))
            {
                return new Guarded(null, Forbidden: true);
            }
        }
        var discovered = await db.Database.SqlQuery<Discovered>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",r.id as "CaseId",r.candidate_generation as "Generation"
            from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id where a.id={appealId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (discovered is null)
        {
            return new Guarded(null);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var present = await GuardUsersAsync(db, [actor.UserId, discovered.OwnerId], cancellationToken);
        if (!await AuthorizeReadAsync(db, access, actor, cancellationToken))
        {
            return new Guarded(null, Forbidden: true);
        }
        if (!present)
        {
            return new Guarded(null);
        }
        var kind = PhotoVariants.Name(variant);
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={discovered.UploadId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={discovered.CaseId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={discovered.Generation} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            select 1 from media.review_candidate_variants where generation={discovered.Generation} and kind={kind} for share
            """, cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_appeals where id={appealId} for share", cancellationToken);
        if (!await AuthorizeReadAsync(db, access, actor, cancellationToken))
        {
            return new Guarded(null, Forbidden: true);
        }
        var now = _time.GetUtcNow();
        var canonical = ObjectKey.ForReviewCandidate(discovered.Generation, variant);
        // Histories are append-only: protect their mutable parents, never ask for UPDATE rights just to lock facts.
        var snapshot = await db.Database.SqlQuery<Snapshot>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",u.version as "UploadVersion",
              r.candidate_generation as "Generation",r.version as "CaseVersion",r.policy_version as "PolicyVersion",
              r.review_deadline as "ReviewDeadline",c.retention_until as "RetentionUntil",
              h.size_bytes as "SizeBytes",h.sha256 as "Sha256",d.id as "OriginalDecisionId",d.decided_at as "OriginalRejectedAt",
              a.version as "AppealVersion",s.id as "AssignmentId",v.size_bytes as "CurrentSizeBytes",
              v.sha256 as "CurrentSha256",v.object_key as "CurrentObjectKey"
            from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id join identity.users owner on owner.id=u.owner_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            join safety.photo_review_decisions d on d.id=a.original_decision_id and d.case_id=r.id
              and d.candidate_generation=c.generation and d.policy_version=r.policy_version
            join safety.photo_review_decision_variants h on h.decision_id=d.id and h.variant={kind}
            join safety.photo_review_appeal_assignments s on s.appeal_id=a.id and s.appeal_version=a.version
            left join media.review_candidate_variants v on v.generation=c.generation and v.kind={kind}
            where a.id={appealId} and r.id={discovered.CaseId} and u.id={discovered.UploadId}
              and u.owner_id={discovered.OwnerId} and r.candidate_generation={discovered.Generation}
              and a.owner_id=u.owner_id and a.candidate_generation=c.generation and a.policy_version=r.policy_version
              and a.status='Open' and a.closed_at is null and a.version>=2
              and s.reviewer_id={actor.UserId} and d.actor_id<>{actor.UserId}
              and r.policy_version='human-review-v1' and d.appeal_id is null
              and d.decision='reject' and d.reason_code='does_not_meet_policy'
              and d.case_version=r.version-1 and a.original_rejected_at=d.decided_at
              and d.candidate_retention_until_snapshot=d.decided_at+interval '168 hours'
              and a.requested_at>=d.decided_at and a.requested_at<d.decided_at+interval '168 hours'
              and a.response_target_at=a.requested_at+interval '168 hours'
              and a.maximum_retention_until=d.decided_at+interval '720 hours'
              and {now}>=a.requested_at and {now}<d.decided_at+interval '720 hours'
              and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_REJECTED'
              and u.version>=3 and c.upload_version=u.version-3
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active','Restricted','Suspended')
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.retention_until=a.maximum_retention_until
              and c.cleanup_requested_at is null and c.cleanup_audit_id is null and c.deleted_at is null
              and c.cleanup_token is null and c.cleanup_lease_until is null
            """).SingleOrDefaultAsync(cancellationToken);
        if (snapshot is null || (expected is not null && !Matches(expected, snapshot)))
        {
            return new Guarded(null);
        }
        if (snapshot.CurrentObjectKey != canonical.Value || snapshot.CurrentSizeBytes != snapshot.SizeBytes
            || snapshot.CurrentSha256 is null || !CryptographicOperations.FixedTimeEquals(snapshot.Sha256, snapshot.CurrentSha256))
        {
            return new Guarded(null, Unavailable: true);
        }
        if (audit)
        {
            var id = Guid.CreateVersion7(now);
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.photo_review_appeal_evidence_accesses
                  (id,appeal_id,appeal_version,assignment_id,actor_id,original_decision_id,candidate_generation,
                    policy_version,variant,variant_sha256,occurred_at)
                values({id},{appealId},{snapshot.AppealVersion},{snapshot.AssignmentId},{actor.UserId},
                  {snapshot.OriginalDecisionId},{snapshot.Generation},{snapshot.PolicyVersion},{kind},{snapshot.Sha256},{now})
                """, cancellationToken);
            await new AuditTrail(db, _time).AppendAsync(new AuditRecord(AuditActorKind.Moderator, actor.UserId,
                AuditAction.PhotoReviewAppealEvidenceViewed, AuditTargetTypes.PhotoReviewEvidenceAccess, id,
                targetVersion: snapshot.AppealVersion), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new Guarded(snapshot);
    }

    private static async Task<bool> AuthorizeReadAsync(DuoraDbContext db, PhotoReviewAccess access,
        PhotoReviewActor actor, CancellationToken cancellationToken)
    {
        // Evidence proves live MFA/session/role. The extra Appeal grant is read under that same actor guard;
        // this read never invokes or weakens the decision capability's recent-TOTP requirement.
        return await access.AuthorizeAsync(actor, PhotoReviewCapability.Evidence, cancellationToken)
            && await db.Database.SqlQuery<bool>($"""
                select coalesce((select enabled from safety.photo_review_grants where user_id={actor.UserId}
                  and capability='appeal' order by version desc limit 1),false) as "Value"
                """).SingleAsync(cancellationToken);
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
            var rows = await db.Database.SqlQuery<Guid>($"""
                select user_id as "Value" from safety.user_guards where user_id={user} for update
                """).ToListAsync(cancellationToken);
            present &= rows.Count == 1;
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

    private static bool Matches(Snapshot first, Snapshot current) => first.OwnerId == current.OwnerId
        && first.UploadId == current.UploadId && first.UploadVersion == current.UploadVersion
        && first.Generation == current.Generation && first.CaseVersion == current.CaseVersion
        && first.PolicyVersion == current.PolicyVersion && first.ReviewDeadline == current.ReviewDeadline
        && first.RetentionUntil == current.RetentionUntil && first.SizeBytes == current.SizeBytes
        && first.OriginalDecisionId == current.OriginalDecisionId && first.OriginalRejectedAt == current.OriginalRejectedAt
        && first.AppealVersion == current.AppealVersion && first.AssignmentId == current.AssignmentId
        && CryptographicOperations.FixedTimeEquals(first.Sha256, current.Sha256);

    private sealed record Guarded(Snapshot? Snapshot, bool Forbidden = false, bool Unavailable = false);
    private sealed record Discovered(Guid OwnerId, Guid UploadId, Guid CaseId, Guid Generation);
    private sealed record Snapshot(Guid OwnerId, Guid UploadId, long UploadVersion, Guid Generation, long CaseVersion,
        string PolicyVersion, DateTimeOffset ReviewDeadline, DateTimeOffset RetentionUntil, long SizeBytes, byte[] Sha256,
        Guid OriginalDecisionId, DateTimeOffset OriginalRejectedAt, long AppealVersion, Guid AssignmentId,
        long? CurrentSizeBytes, byte[]? CurrentSha256, string? CurrentObjectKey)
    {
        public override string ToString() => "PhotoReviewAppealEvidenceSnapshot";
    }
}
