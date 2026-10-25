using System.Data;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

/// <summary>Initial-review reconciliation, never a human decision or a restore health certificate.</summary>
internal sealed class PhotoReviewCandidateAvailability(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
{
    private static readonly string[] VariantNames = ["card", "full", "thumb"];

    internal async Task<int> ReconcileAsync(Guid caseId, CancellationToken cancellationToken)
    {
        RequireOwnTransaction();
        var initial = await GuardedAsync(caseId, expected: null, unavailable: false, cancellationToken);
        if (initial is null)
        {
            return 0;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var unavailable = false;
        foreach (var variant in initial.Variants)
        {
            if (!await AvailableAsync(variant, timeout.Token))
            {
                unavailable = true;
                break;
            }
        }
        timeout.Token.ThrowIfCancellationRequested();
        if (!unavailable)
        {
            return 0;
        }
        return await GuardedAsync(caseId, initial, unavailable: true, cancellationToken) is null ? 0 : 1;
    }

    private static void RequireOwnTransaction()
    {
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Candidate availability owns its complete READ COMMITTED phases.");
        }
    }

    private async Task<bool> AvailableAsync(Variant variant, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            await using var stream = await store.OpenReadAsync(ObjectKey.Parse(variant.ObjectKey), cancellationToken);
            if (stream is null)
            {
                return false;
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long read = 0;
            while (read < variant.SizeBytes)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, variant.SizeBytes - read)), cancellationToken);
                if (count == 0)
                {
                    return false;
                }
                hash.AppendData(buffer, 0, count);
                read += count;
            }
            return await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) == 0
                && CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), variant.Sha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private async Task<Snapshot?> GuardedAsync(Guid caseId, Snapshot? expected, bool unavailable, CancellationToken cancellationToken)
    {
        RequireOwnTransaction();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var discovered = await db.Database.SqlQuery<Discovered>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",r.candidate_generation as "Generation"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id where r.id={caseId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (discovered is null)
        {
            return null;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await db.Database.ExecuteSqlAsync($"set local lock_timeout='2s'", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.user_guards(user_id) select id from identity.users where id={discovered.OwnerId} on conflict do nothing
            """, cancellationToken);
        var guard = await db.Database.SqlQuery<Guid>($"""
            select user_id as "Value" from safety.user_guards where user_id={discovered.OwnerId} for update
            """).ToListAsync(cancellationToken);
        if (guard.Count != 1)
        {
            return null;
        }
        await db.Database.ExecuteSqlAsync($"select 1 from identity.users where id={discovered.OwnerId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={discovered.UploadId} for update", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={caseId} for update", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={discovered.Generation} for update", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            select 1 from media.review_candidate_variants where generation={discovered.Generation} order by kind for share
            """, cancellationToken);
        var now = time.GetUtcNow();
        var state = await db.Database.SqlQuery<State>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",u.version as "UploadVersion",r.version as "CaseVersion",
              c.generation as "Generation",r.review_deadline as "ReviewDeadline",c.retention_until as "RetentionUntil",
              c.committed_at as "CommittedAt",c.writer_settled_at as "WriterSettledAt",owner.status as "OwnerStatus"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id
            join identity.users owner on owner.id=u.owner_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            where r.id={caseId} and u.id={discovered.UploadId} and u.owner_id={discovered.OwnerId}
              and c.generation={discovered.Generation} and r.policy_version='human-review-v1'
              and u.state='AwaitingReview' and u.version<9223372036854775807 and r.version<9223372036854775807
              and c.upload_version=u.version-1 and c.retention_until=r.review_deadline+interval '24 hours'
              and c.retention_until>{now} and owner.email_confirmed and owner.status in ('PendingEligibility','Active','Restricted','Suspended')
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.cleanup_audit_id is null and c.deleted_at is null
              and c.cleanup_token is null and c.cleanup_lease_until is null
              and not exists(select 1 from safety.photo_review_decisions d where d.case_id=r.id)
              and not exists(select 1 from safety.photo_review_appeals a where a.case_id=r.id)
            """).SingleOrDefaultAsync(cancellationToken);
        if (state is null)
        {
            return null;
        }
        var variants = await db.Database.SqlQuery<Variant>($"""
            select kind as "Kind",object_key as "ObjectKey",size_bytes as "SizeBytes",sha256 as "Sha256"
            from media.review_candidate_variants where generation={state.Generation} order by kind
            """).ToArrayAsync(cancellationToken);
        if (variants.Length != 3 || !variants.Select(v => v.Kind).SequenceEqual(VariantNames)
            || variants.Any(v => v.SizeBytes is < 1 or > 16_777_216 || v.Sha256.Length != 32
                || !PhotoVariants.All.Any(kind => PhotoVariants.Name(kind) == v.Kind
                    && v.ObjectKey == ObjectKey.ForReviewCandidate(state.Generation, kind).Value)))
        {
            return null;
        }
        var snapshot = new Snapshot(state, variants);
        if (expected is not null && (expected.State != state || !SameVariants(expected.Variants, variants)))
        {
            return null;
        }
        if (unavailable)
        {
            var uploadVersion = checked(state.UploadVersion + 1);
            var caseVersion = checked(state.CaseVersion + 1);
            var changedUpload = await db.Database.ExecuteSqlAsync($"""
                update media.uploads set state='Rejected',rejection_code={PhotoReviewResults.Unavailable},version={uploadVersion}
                where id={state.UploadId} and state='AwaitingReview' and version={state.UploadVersion}
                """, cancellationToken);
            var changedCase = await db.Database.ExecuteSqlAsync($"""
                update safety.photo_review_cases set version={caseVersion} where id={caseId} and version={state.CaseVersion}
                """, cancellationToken);
            await new AuditTrail(db, time).AppendAsync(new AuditRecord(AuditActorKind.System, null,
                AuditAction.PhotoReviewUnavailable, AuditTargetTypes.PhotoReviewCase, caseId, caseVersion), cancellationToken);
            var auditId = await db.Database.SqlQuery<long>($"""
                select id as "Value" from ops.audit_events where actor_kind='System' and actor_id is null
                  and action={AuditAction.PhotoReviewUnavailable.Code} and target_type={AuditTargetTypes.PhotoReviewCase}
                  and target_id={caseId} and target_version={caseVersion}
                """).SingleAsync(cancellationToken);
            if (auditId <= 0)
            {
                throw new InvalidOperationException("Unavailable reconciliation requires one real audit identity.");
            }
            var changedCandidate = await db.Database.ExecuteSqlAsync($"""
                update media.review_candidates set cleanup_requested_at={now},cleanup_available_at=least(cleanup_available_at,{now}),
                  cleanup_audit_id={auditId} where generation={state.Generation} and cleanup_requested_at is null
                  and cleanup_audit_id is null and deleted_at is null and cleanup_token is null
                """, cancellationToken);
            if (changedUpload != 1 || changedCase != 1 || changedCandidate != 1)
            {
                throw new InvalidOperationException("All guarded unavailable projections must change exactly once.");
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private static bool SameVariants(Variant[] first, Variant[] current) => first.Zip(current).All(pair =>
        pair.First.Kind == pair.Second.Kind && pair.First.ObjectKey == pair.Second.ObjectKey
        && pair.First.SizeBytes == pair.Second.SizeBytes
        && CryptographicOperations.FixedTimeEquals(pair.First.Sha256, pair.Second.Sha256));

    private sealed record Discovered(Guid OwnerId, Guid UploadId, Guid Generation);
    private sealed record State(Guid OwnerId, Guid UploadId, long UploadVersion, long CaseVersion, Guid Generation,
        DateTimeOffset ReviewDeadline, DateTimeOffset RetentionUntil, DateTimeOffset CommittedAt, DateTimeOffset WriterSettledAt, string OwnerStatus);
    private sealed record Variant(string Kind, string ObjectKey, long SizeBytes, byte[] Sha256)
    {
        public override string ToString() => "CandidateAvailabilityVariant";
    }
    private sealed record Snapshot(State State, Variant[] Variants);
}
