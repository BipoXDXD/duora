using System.Data;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

/// <summary>System closure after the hard limit or confirmed lost evidence; never a human merits decision.</summary>
internal sealed class PhotoReviewAppealAvailability(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
{
    private static readonly string[] VariantNames = ["card", "full", "thumb"];

    internal async Task<int> ReconcileAsync(Guid appealId, CancellationToken ct)
    {
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Appeal reconciliation owns complete READ COMMITTED phases.");
        }
        var snapshot = await PhaseAsync(appealId, null, false, ct);
        if (snapshot is null)
        {
            return 0;
        }
        var unavailable = HasExpired(snapshot.State) || !ValidManifest(snapshot);
        if (!unavailable)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(15));
            foreach (var variant in snapshot.Variants)
            {
                if (!await AvailableAsync(snapshot.State.Generation, variant, budget.Token))
                {
                    unavailable = true;
                    break;
                }
            }
            budget.Token.ThrowIfCancellationRequested();
            unavailable |= HasExpired(snapshot.State);
        }
        return unavailable && await PhaseAsync(appealId, snapshot, true, ct) is not null ? 1 : 0;
    }

    private bool HasExpired(State state) => time.GetUtcNow() >= state.Maximum;

    private async Task<bool> AvailableAsync(Guid generation, Variant variant, CancellationToken ct)
    {
        var kind = PhotoVariants.All.Single(v => PhotoVariants.Name(v) == variant.Kind);
        var buffer = new byte[64 * 1024];
        try
        {
            await using var stream = await store.OpenReadAsync(ObjectKey.ForReviewCandidate(generation, kind), ct);
            if (stream is null)
            {
                return false;
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long read = 0;
            while (read < variant.SizeBytes)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, variant.SizeBytes - read)), ct);
                if (count == 0)
                {
                    return false;
                }
                hash.AppendData(buffer, 0, count);
                read += count;
            }
            return await stream.ReadAsync(buffer.AsMemory(0, 1), ct) == 0
                && CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), variant.Sha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private async Task<Snapshot?> PhaseAsync(Guid appealId, Snapshot? expected, bool close, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var discovery = await db.Database.SqlQuery<Discovered>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",r.id as "CaseId",r.candidate_generation as "Generation"
            from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id where a.id={appealId}
            """).SingleOrDefaultAsync(ct);
        if (discovery is null)
        {
            return null;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlAsync($"set local lock_timeout='2s'", ct);
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.user_guards(user_id) select id from identity.users where id={discovery.OwnerId} on conflict do nothing
            """, ct);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.user_guards where user_id={discovery.OwnerId} for update", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from identity.users where id={discovery.OwnerId} for share", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={discovery.UploadId} for update", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={discovery.CaseId} for update", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={discovery.Generation} for update", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidate_variants where generation={discovery.Generation} order by kind for share", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_appeals where id={appealId} for update", ct);
        var row = await db.Database.SqlQuery<State>($"""
            select a.version as "AppealVersion",r.version as "CaseVersion",u.version as "PhotoVersion",
              c.generation as "Generation",a.maximum_retention_until as "Maximum",a.requested_at as "RequestedAt"
            from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id join identity.users owner on owner.id=u.owner_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            join safety.photo_review_decisions d on d.id=a.original_decision_id and d.case_id=r.id
              and d.candidate_generation=c.generation and d.policy_version=r.policy_version
            where a.id={appealId} and r.id={discovery.CaseId} and u.id={discovery.UploadId}
              and u.owner_id={discovery.OwnerId} and r.candidate_generation={discovery.Generation}
              and a.owner_id=u.owner_id and a.candidate_generation=c.generation and a.policy_version=r.policy_version
              and a.status='Open' and a.closed_at is null and a.version<9223372036854775807
              and r.version<9223372036854775807 and u.version<9223372036854775807
              and d.appeal_id is null and d.decision='reject' and d.reason_code='does_not_meet_policy'
              and r.policy_version='human-review-v1' and d.case_version=r.version-1
              and a.original_rejected_at=d.decided_at and a.maximum_retention_until=d.decided_at+interval '720 hours'
              and a.requested_at>=d.decided_at and a.requested_at<d.decided_at+interval '168 hours'
              and a.response_target_at=a.requested_at+interval '168 hours'
              and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_REJECTED' and c.upload_version=u.version-3
              and c.retention_until=a.maximum_retention_until
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.cleanup_audit_id is null and c.deleted_at is null
              and c.cleanup_token is null and c.cleanup_lease_until is null
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active','Restricted','Suspended')
              and not exists(select 1 from safety.photo_review_decisions subsequent where subsequent.appeal_id=a.id)
            """).SingleOrDefaultAsync(ct);
        if (row is null || time.GetUtcNow() < row.RequestedAt)
        {
            return null;
        }
        var variants = await db.Database.SqlQuery<Variant>($"""
            select h.variant as "Kind",h.size_bytes as "SizeBytes",h.sha256 as "Sha256",
              v.object_key as "CurrentKey",v.size_bytes as "CurrentSize",v.sha256 as "CurrentSha256"
            from safety.photo_review_appeals a join safety.photo_review_decision_variants h on h.decision_id=a.original_decision_id
            left join media.review_candidate_variants v on v.generation=a.candidate_generation and v.kind=h.variant
            where a.id={appealId} order by h.variant
            """).ToArrayAsync(ct);
        if (expected is not null && (expected.State != row || expected.Variants.Length != variants.Length
            || !expected.Variants.Zip(variants).All(v => v.First.Kind == v.Second.Kind
                && v.First.SizeBytes == v.Second.SizeBytes && v.First.CurrentKey == v.Second.CurrentKey
                && v.First.CurrentSize == v.Second.CurrentSize
                && CryptographicOperations.FixedTimeEquals(v.First.Sha256, v.Second.Sha256)
                && ((v.First.CurrentSha256 is null && v.Second.CurrentSha256 is null)
                    || (v.First.CurrentSha256 is { } first && v.Second.CurrentSha256 is { } second
                        && CryptographicOperations.FixedTimeEquals(first, second))))))
        {
            return null;
        }
        if (close)
        {
            var instant = time.GetUtcNow();
            var now = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
            var version = checked(row.AppealVersion + 1);
            var retention = now < row.Maximum ? now : row.Maximum;
            var root = await db.Database.ExecuteSqlAsync($"""
                update safety.photo_review_appeals set status='ClosedUnavailable',closed_at={now},version={version}
                where id={appealId} and status='Open' and closed_at is null and version={row.AppealVersion}
                """, ct);
            var upload = await db.Database.ExecuteSqlAsync($"update media.uploads set version=version+1 where id={discovery.UploadId} and version={row.PhotoVersion}", ct);
            var review = await db.Database.ExecuteSqlAsync($"update safety.photo_review_cases set version=version+1 where id={discovery.CaseId} and version={row.CaseVersion}", ct);
            await new AuditTrail(db, time).AppendAsync(new AuditRecord(AuditActorKind.System, null,
                AuditAction.PhotoReviewAppealUnavailable, AuditTargetTypes.PhotoReviewAppeal, appealId, targetVersion: version), ct);
            var audit = await db.Database.SqlQuery<long>($"""
                select id as "Value" from ops.audit_events where actor_kind='System' and actor_id is null
                  and action='safety.photo_review_appeal_unavailable' and target_type='photo_review_appeal'
                  and target_id={appealId} and target_version={version}
                """).SingleAsync(ct);
            var candidate = await db.Database.ExecuteSqlAsync($"""
                update media.review_candidates set retention_until={retention},cleanup_requested_at={now},cleanup_audit_id={audit},
                  cleanup_available_at=least(cleanup_available_at,{now})
                where generation={row.Generation} and cleanup_requested_at is null and cleanup_audit_id is null
                  and cleanup_token is null and deleted_at is null and retention_until={row.Maximum}
                """, ct);
            if (root != 1 || upload != 1 || review != 1 || candidate != 1 || audit <= 0)
            {
                throw new InvalidOperationException("All unavailable appeal projections require one committed audit.");
            }
        }
        await transaction.CommitAsync(ct);
        return new(row, variants);
    }

    private sealed record Discovered(Guid OwnerId, Guid UploadId, Guid CaseId, Guid Generation);
    private sealed record State(long AppealVersion, long CaseVersion, long PhotoVersion, Guid Generation,
        DateTimeOffset Maximum, DateTimeOffset RequestedAt);
    // Missing or altered mutable metadata is itself unavailability, never permission to make a human decision.
    private static bool ValidManifest(Snapshot snapshot) => snapshot.Variants.Length == 3
        && snapshot.Variants.Select(v => v.Kind).SequenceEqual(VariantNames)
        && snapshot.Variants.All(v => v.SizeBytes is > 0 and <= 16_777_216 && v.Sha256.Length == 32
            && v.CurrentSize == v.SizeBytes && v.CurrentSha256 is { Length: 32 } currentHash
            && CryptographicOperations.FixedTimeEquals(currentHash, v.Sha256)
            && PhotoVariants.All.Any(kind => PhotoVariants.Name(kind) == v.Kind
                && v.CurrentKey == ObjectKey.ForReviewCandidate(snapshot.State.Generation, kind).Value))
        && snapshot.Variants.Sum(v => v.SizeBytes) <= 25_165_824;

    private sealed record Variant(string Kind, long SizeBytes, byte[] Sha256, string? CurrentKey, long? CurrentSize, byte[]? CurrentSha256);
    private sealed record Snapshot(State State, Variant[] Variants);
}
