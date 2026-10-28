using System.Data;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

internal enum PhotoReviewRejectionReason
{
    DoesNotMeetPolicy,
}

internal abstract record PhotoReviewRejectionResult
{
    internal sealed record Rejected(Guid CaseId, long Version, long PhotoVersion) : PhotoReviewRejectionResult;
    internal sealed record Forbidden : PhotoReviewRejectionResult;
    internal sealed record NotFound : PhotoReviewRejectionResult;
    internal sealed record PreconditionFailed : PhotoReviewRejectionResult;
    internal sealed record KeyReused : PhotoReviewRejectionResult;
    internal sealed record StateConflict : PhotoReviewRejectionResult;
    internal sealed record Unavailable : PhotoReviewRejectionResult;
}

/// <summary>Initial human rejection only; never publishes, sanctions an account or deletes retained evidence.</summary>
internal sealed class PhotoReviewRejections
{
    private static readonly TimeSpan CandidateCleanupWindow = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<DuoraDbContext> _contexts;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;

    internal PhotoReviewRejections(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _contexts = contexts;
        _store = store;
        _time = time;
    }

    internal async Task<PhotoReviewRejectionResult> RejectAsync(PhotoReviewActor actor, Guid caseId, long expectedVersion,
        PhotoReviewRejectionReason reason, IdempotencyKey key, CancellationToken cancellationToken)
    {
        var reasonCode = reason switch
        {
            PhotoReviewRejectionReason.DoesNotMeetPolicy => "does_not_meet_policy",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown human rejection reason."),
        };
        if (!IdempotencyKey.TryParse(key.Value, out _))
        {
            throw new ArgumentException("A validated idempotency key is required.", nameof(key));
        }
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Human decisions own complete READ COMMITTED phases.");
        }
        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value));
        var intent = SHA256.HashData(Encoding.UTF8.GetBytes($"reject\n{caseId:D}\n{reasonCode}"));
        var initial = await PhaseAsync(actor, caseId, expectedVersion, keyHash, intent, reasonCode,
            expected: null, commit: false, cancellationToken);
        if (initial.Result is { } result)
        {
            return result;
        }
        var snapshot = initial.Evidence ?? throw new InvalidOperationException("A guarded candidate snapshot is required.");
        if (!await VerifyBytesAsync(snapshot, cancellationToken))
        {
            return new PhotoReviewRejectionResult.Unavailable();
        }
        var final = await PhaseAsync(actor, caseId, expectedVersion, keyHash, intent, reasonCode, snapshot, commit: true, cancellationToken);
        return final.Result ?? throw new InvalidOperationException("A human decision needs a confirmed result.");
    }

    private async Task<Phase> PhaseAsync(PhotoReviewActor actor, Guid caseId, long expectedVersion,
        byte[] keyHash, byte[] intent, string reason, Evidence? expected, bool commit, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var access = new PhotoReviewAccess(db, _time);
        await using (var preauthorization = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
            {
                return new Phase(null, new PhotoReviewRejectionResult.Forbidden());
            }
            if (await ReplayAsync(db, actor.UserId, keyHash, intent, cancellationToken) is { } replay)
            {
                return new Phase(null, replay);
            }
        }
        var discovered = await db.Database.SqlQuery<Discovered>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",r.candidate_generation as "Generation"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id where r.id={caseId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (discovered is null)
        {
            return new Phase(null, new PhotoReviewRejectionResult.NotFound());
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var present = await GuardUsersAsync(db, [actor.UserId, discovered.OwnerId], cancellationToken);
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
        {
            return new Phase(null, new PhotoReviewRejectionResult.Forbidden());
        }
        if (await ReplayAsync(db, actor.UserId, keyHash, intent, cancellationToken) is { } confirmed)
        {
            return new Phase(null, confirmed);
        }
        if (!present)
        {
            return new Phase(null, new PhotoReviewRejectionResult.NotFound());
        }
        await LockBusinessRowsAsync(db, caseId, discovered, commit, cancellationToken);
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
        {
            return new Phase(null, new PhotoReviewRejectionResult.Forbidden());
        }
        var row = await db.Database.SqlQuery<CaseState>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",u.version as "UploadVersion",u.state as "State",
              r.candidate_generation as "Generation",r.version as "CaseVersion",r.policy_version as "PolicyVersion",
              r.review_deadline as "ReviewDeadline",c.retention_until as "RetentionUntil",c.upload_version as "PreparedVersion"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id
            join identity.users owner on owner.id=u.owner_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.upload_id=u.id and c.case_id=r.id
            where r.id={caseId} and u.id={discovered.UploadId} and u.owner_id={discovered.OwnerId}
              and r.candidate_generation={discovered.Generation}
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active')
              and (u.state<>'AwaitingReview' or (c.committed_at is not null and c.writer_settled_at is not null
                and c.cleanup_requested_at is null and c.deleted_at is null))
              and (select a.reviewer_id from safety.photo_review_assignments a where a.case_id=r.id
                order by a.case_version desc limit 1)={actor.UserId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (row is null || row.State == "Deleted")
        {
            return new Phase(null, new PhotoReviewRejectionResult.NotFound());
        }
        if (row.State != "AwaitingReview")
        {
            return new Phase(null, new PhotoReviewRejectionResult.StateConflict());
        }
        if (row.CaseVersion != expectedVersion)
        {
            return new Phase(null, new PhotoReviewRejectionResult.PreconditionFailed());
        }
        var variants = await db.Database.SqlQuery<Variant>($"""
            select kind as "Kind",size_bytes as "SizeBytes",sha256 as "Sha256"
            from media.review_candidate_variants where generation={row.Generation}
              and object_key='review-candidates/' || replace({row.Generation}::text,'-','') || '-' || kind
            order by kind
            """).ToListAsync(cancellationToken);
        if (!ValidManifest(variants))
        {
            return new Phase(null, new PhotoReviewRejectionResult.Unavailable());
        }
        var evidence = new Evidence(row, [.. variants]);
        if (expected is not null && !Matches(expected, evidence))
        {
            return new Phase(null, new PhotoReviewRejectionResult.StateConflict());
        }
        // Time and step-up are checked after every business lock and all metadata reads, again at the effect.
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
        {
            return new Phase(null, new PhotoReviewRejectionResult.Forbidden());
        }
        var now = _time.GetUtcNow();
        if (row.ReviewDeadline <= now || row.RetentionUntil <= now || row.UploadVersion != row.PreparedVersion + 1)
        {
            return new Phase(null, new PhotoReviewRejectionResult.StateConflict());
        }
        if (!commit)
        {
            await transaction.CommitAsync(cancellationToken);
            return new Phase(evidence, null);
        }
        var caseVersion = checked(row.CaseVersion + 1);
        var photoVersion = checked(row.UploadVersion + 1);
        var id = Guid.CreateVersion7(now);
        // No appeal exists (owner decision of 2026-10-28): the rejected candidate enters the same bounded cleanup window
        // as an approved one. The owner may upload a new photo, which starts a new review.
        var candidateRetention = now.Add(CandidateCleanupWindow);
        var historyRetention = now.AddYears(1);
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_decisions(id,case_id,case_version,actor_id,decision,reason_code,
              candidate_generation,policy_version,decided_at,candidate_retention_until_snapshot,history_retain_until)
            values({id},{caseId},{caseVersion},{actor.UserId},'reject',{reason},{row.Generation},{row.PolicyVersion},
              {now},{candidateRetention},{historyRetention})
            """, cancellationToken);
        foreach (var variant in variants)
        {
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.photo_review_decision_variants(decision_id,variant,size_bytes,sha256)
                values({id},{variant.Kind},{variant.SizeBytes},{variant.Sha256})
                """, cancellationToken);
        }
        var changedUpload = await db.Database.ExecuteSqlAsync($"""
            update media.uploads set state='Rejected',rejection_code='PHOTO_REVIEW_REJECTED',version={photoVersion}
            where id={row.UploadId} and state='AwaitingReview' and version={row.UploadVersion}
            """, cancellationToken);
        var changedCase = await db.Database.ExecuteSqlAsync($"""
            update safety.photo_review_cases set version={caseVersion} where id={caseId} and version={row.CaseVersion}
            """, cancellationToken);
        var changedCandidate = await db.Database.ExecuteSqlAsync($"""
            update media.review_candidates
            set retention_until={candidateRetention},cleanup_requested_at={now},
              cleanup_available_at=least(cleanup_available_at,{now})
            where generation={row.Generation} and case_id={caseId} and cleanup_requested_at is null and deleted_at is null
            """, cancellationToken);
        if (changedUpload != 1 || changedCase != 1 || changedCandidate != 1)
        {
            throw new InvalidOperationException("All guarded human decision projections must change exactly once.");
        }
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_decision_commands(actor_id,operation,key_hash,case_id,intent_sha256,
              result_version,result_photo_version,created_at)
            values({actor.UserId},'decide',{keyHash},{caseId},{intent},{caseVersion},{photoVersion},{now})
            """, cancellationToken);
        await new AuditTrail(db, _time).AppendAsync(new AuditRecord(AuditActorKind.Moderator, actor.UserId,
            AuditAction.PhotoReviewRejected, AuditTargetTypes.PhotoReviewDecision, id, targetVersion: caseVersion), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Phase(null, new PhotoReviewRejectionResult.Rejected(caseId, caseVersion, photoVersion));
    }

    private async Task<bool> VerifyBytesAsync(Evidence evidence, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            foreach (var variant in evidence.Variants)
            {
                var buffer = new byte[checked((int)variant.SizeBytes)];
                var excess = new byte[1];
                try
                {
                    var kind = PhotoVariants.All.Single(value => PhotoVariants.Name(value) == variant.Kind);
                    await using var stream = await _store.OpenReadAsync(ObjectKey.ForReviewCandidate(evidence.State.Generation, kind), budget.Token);
                    if (stream is null)
                    {
                        return false;
                    }
                    var offset = 0;
                    while (offset < buffer.Length)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(offset), budget.Token);
                        if (count == 0)
                        {
                            return false;
                        }
                        offset += count;
                    }
                    if (await stream.ReadAsync(excess, budget.Token) != 0
                        || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(buffer), variant.Sha256))
                    {
                        return false;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                    CryptographicOperations.ZeroMemory(excess);
                }
            }
            budget.Token.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool ValidManifest(List<Variant> variants) => variants.Count == 3
        && variants.Select(v => v.Kind).SequenceEqual(PhotoVariants.All.Select(PhotoVariants.Name).Order())
        && variants.All(v => v.SizeBytes is > 0 and <= 16_777_216 && v.Sha256.Length == 32)
        && variants.Sum(v => v.SizeBytes) <= 25_165_824;

    private static bool Matches(Evidence expected, Evidence current) => expected.State == current.State
        && expected.Variants.Zip(current.Variants).All(pair => pair.First.Kind == pair.Second.Kind
            && pair.First.SizeBytes == pair.Second.SizeBytes
            && CryptographicOperations.FixedTimeEquals(pair.First.Sha256, pair.Second.Sha256));

    private static async Task<PhotoReviewRejectionResult?> ReplayAsync(DuoraDbContext db, Guid actorId,
        byte[] keyHash, byte[] intent, CancellationToken cancellationToken)
    {
        var receipt = await db.Database.SqlQuery<Receipt>($"""
            select case_id as "CaseId",intent_sha256 as "Intent",result_version as "CaseVersion",result_photo_version as "PhotoVersion"
            from safety.photo_review_decision_commands where actor_id={actorId} and operation='decide' and key_hash={keyHash}
            """).SingleOrDefaultAsync(cancellationToken);
        return receipt is null ? null : CryptographicOperations.FixedTimeEquals(receipt.Intent, intent)
            ? new PhotoReviewRejectionResult.Rejected(receipt.CaseId, receipt.CaseVersion, receipt.PhotoVersion)
            : new PhotoReviewRejectionResult.KeyReused();
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

    private static async Task LockBusinessRowsAsync(DuoraDbContext db, Guid caseId, Discovered item, bool writing, CancellationToken cancellationToken)
    {
        if (writing)
        {
            await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={item.UploadId} for update", cancellationToken);
            await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={caseId} for update", cancellationToken);
            await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={item.Generation} for update", cancellationToken);
        }
        else
        {
            await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={item.UploadId} for share", cancellationToken);
            await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={caseId} for share", cancellationToken);
            await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={item.Generation} for share", cancellationToken);
        }
        await db.Database.ExecuteSqlAsync($"""
            select 1 from media.review_candidate_variants where generation={item.Generation} order by kind for share
            """, cancellationToken);
    }

    private sealed record Phase(Evidence? Evidence, PhotoReviewRejectionResult? Result);
    private sealed record Discovered(Guid OwnerId, Guid UploadId, Guid Generation);
    private sealed record Receipt(Guid CaseId, byte[] Intent, long CaseVersion, long PhotoVersion);
    private sealed record Variant(string Kind, long SizeBytes, byte[] Sha256);
    private sealed record CaseState(Guid OwnerId, Guid UploadId, long UploadVersion, string State, Guid Generation,
        long CaseVersion, string PolicyVersion, DateTimeOffset ReviewDeadline, DateTimeOffset RetentionUntil, long PreparedVersion);
    private sealed record Evidence(CaseState State, Variant[] Variants)
    {
        public override string ToString() => "PhotoReviewDecisionEvidence";
    }
}
