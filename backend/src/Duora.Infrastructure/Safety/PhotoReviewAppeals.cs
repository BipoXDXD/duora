using System.Data;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

internal abstract record PhotoReviewAppealRequestResult
{
    internal sealed record Accepted(Guid AppealId, Guid PhotoId, long PhotoVersion, DateTimeOffset RequestedAt,
        DateTimeOffset ResponseTargetAt, DateTimeOffset MaximumRetentionUntil) : PhotoReviewAppealRequestResult;
    internal sealed record Forbidden : PhotoReviewAppealRequestResult;
    internal sealed record NotFound : PhotoReviewAppealRequestResult;
    internal sealed record PreconditionFailed : PhotoReviewAppealRequestResult;
    internal sealed record KeyReused : PhotoReviewAppealRequestResult;
    internal sealed record StateConflict : PhotoReviewAppealRequestResult;
    internal sealed record Unavailable : PhotoReviewAppealRequestResult;
}

/// <summary>Owner request only; preserves the original human rejection. Not registered or exposed.</summary>
internal sealed class PhotoReviewAppeals
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;

    internal PhotoReviewAppeals(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _contexts = contexts;
        _store = store;
        _time = time;
    }

    internal async Task<PhotoReviewAppealRequestResult> RequestAsync(PhotoReviewActor owner, Guid photoId,
        long expectedPhotoVersion, IdempotencyKey key, CancellationToken cancellationToken)
    {
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Appeal requests own complete READ COMMITTED phases.");
        }
        if (!IdempotencyKey.TryParse(key.Value, out _))
        {
            throw new ArgumentException("A validated idempotency key is required.", nameof(key));
        }

        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value));
        var intent = SHA256.HashData(Encoding.UTF8.GetBytes($"request-photo-appeal\n{photoId:D}"));
        var initial = await PhaseAsync(owner, photoId, expectedPhotoVersion, keyHash, intent, null, false, cancellationToken);
        if (initial.Result is { } result)
        {
            return result;
        }

        var snapshot = initial.Evidence ?? throw new InvalidOperationException("An appeal needs a guarded rejection snapshot.");
        if (!await VerifyBytesAsync(snapshot, cancellationToken))
        {
            return new PhotoReviewAppealRequestResult.Unavailable();
        }

        var final = await PhaseAsync(owner, photoId, expectedPhotoVersion, keyHash, intent, snapshot, true, cancellationToken);
        return final.Result ?? throw new InvalidOperationException("An appeal request needs a confirmed result.");
    }

    private async Task<Phase> PhaseAsync(PhotoReviewActor owner, Guid photoId, long expectedPhotoVersion,
        byte[] keyHash, byte[] intent, Evidence? expected, bool commit, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await AuthorizeOwnerAsync(db, owner, cancellationToken))
        {
            return new Phase(null, new PhotoReviewAppealRequestResult.Forbidden());
        }
        if (await ReplayAsync(db, owner.UserId, keyHash, intent, cancellationToken) is { } replay)
        {
            return new Phase(null, replay);
        }

        var discovered = await db.Database.SqlQuery<Discovered>($"""
            select u.id as "UploadId",r.id as "CaseId",r.candidate_generation as "Generation"
            from media.uploads u left join safety.photo_review_cases r on r.upload_id=u.id
            where u.id={photoId} and u.owner_id={owner.UserId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (discovered is null)
        {
            return new Phase(null, new PhotoReviewAppealRequestResult.NotFound());
        }
        if (discovered.CaseId is not { } caseId || discovered.Generation is not { } generation)
        {
            return new Phase(null, new PhotoReviewAppealRequestResult.StateConflict());
        }

        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={photoId} for update", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={caseId} for update", cancellationToken);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={generation} for update", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            select 1 from media.review_candidate_variants where generation={generation} order by kind for share
            """, cancellationToken);
        if (!await AuthorizeOwnerAsync(db, owner, cancellationToken))
        {
            return new Phase(null, new PhotoReviewAppealRequestResult.Forbidden());
        }
        // The immutable initial rejection must still be the current case fact. Appeal decisions are never origins.
        var row = await db.Database.SqlQuery<RejectionState>($"""
            select u.id as "UploadId",u.version as "UploadVersion",r.id as "CaseId",r.version as "CaseVersion",
              r.candidate_generation as "Generation",r.policy_version as "PolicyVersion",d.id as "DecisionId",
              d.decided_at as "RejectedAt",d.candidate_retention_until_snapshot as "RetentionUntil"
            from media.uploads u join safety.photo_review_cases r on r.upload_id=u.id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            join safety.photo_review_decisions d on d.case_id=r.id and d.case_version=r.version
              and d.candidate_generation=c.generation and d.policy_version=r.policy_version
            where u.id={photoId} and u.owner_id={owner.UserId} and r.id={caseId} and c.generation={generation}
              and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_REJECTED'
              and r.policy_version='human-review-v1' and d.decision='reject' and d.reason_code='does_not_meet_policy'
              and d.appeal_id is null and c.upload_version=u.version-2
              and c.retention_until=d.candidate_retention_until_snapshot
              and d.candidate_retention_until_snapshot=d.decided_at+interval '168 hours'
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.cleanup_audit_id is null and c.deleted_at is null
              and c.cleanup_token is null and c.cleanup_lease_until is null
              and not exists(select 1 from safety.photo_review_appeals a where a.case_id=r.id)
            """).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return new Phase(null, new PhotoReviewAppealRequestResult.StateConflict());
        }
        if (row.UploadVersion != expectedPhotoVersion)
        {
            return new Phase(null, new PhotoReviewAppealRequestResult.PreconditionFailed());
        }

        // Bind the current private variants to the immutable manifest that the human actually rejected.
        var variants = await db.Database.SqlQuery<Variant>($"""
            select v.kind as "Kind",v.size_bytes as "SizeBytes",v.sha256 as "Sha256"
            from media.review_candidate_variants v join safety.photo_review_decision_variants d
              on d.decision_id={row.DecisionId} and d.variant=v.kind and d.size_bytes=v.size_bytes and d.sha256=v.sha256
            where v.generation={generation}
              and v.object_key='review-candidates/' || replace({generation}::text,'-','') || '-' || v.kind
            order by v.kind
            """).ToListAsync(cancellationToken);
        if (!ValidManifest(variants))
        {
            return new Phase(null, new PhotoReviewAppealRequestResult.Unavailable());
        }

        var evidence = new Evidence(row, [.. variants]);
        if (expected is not null && !Matches(expected, evidence))
        {
            return new Phase(null, new PhotoReviewAppealRequestResult.StateConflict());
        }
        if (!await AuthorizeOwnerAsync(db, owner, cancellationToken))
        {
            return new Phase(null, new PhotoReviewAppealRequestResult.Forbidden());
        }

        var now = _time.GetUtcNow();
        if (now < row.RejectedAt || now >= row.RejectedAt.AddDays(7) || now >= row.RetentionUntil
            || row.UploadVersion == long.MaxValue || row.CaseVersion == long.MaxValue)
        {
            return new Phase(null, new PhotoReviewAppealRequestResult.StateConflict());
        }
        if (!commit)
        {
            await transaction.CommitAsync(cancellationToken);
            return new Phase(evidence, null);
        }

        var appealId = Guid.CreateVersion7(now);
        var photoVersion = checked(row.UploadVersion + 1);
        var caseVersion = checked(row.CaseVersion + 1);
        var target = now.AddDays(7);
        var maximum = row.RejectedAt.AddDays(30);
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_appeals(id,case_id,original_decision_id,owner_id,candidate_generation,
              policy_version,status,version,original_rejected_at,requested_at,response_target_at,maximum_retention_until)
            values({appealId},{caseId},{row.DecisionId},{owner.UserId},{generation},{row.PolicyVersion},'Open',1,
              {row.RejectedAt},{now},{target},{maximum})
            """, cancellationToken);
        var changedUpload = await db.Database.ExecuteSqlAsync($"""
            update media.uploads set version={photoVersion}
            where id={photoId} and owner_id={owner.UserId} and state='Rejected'
              and rejection_code='PHOTO_REVIEW_REJECTED' and version={row.UploadVersion}
            """, cancellationToken);
        var changedCase = await db.Database.ExecuteSqlAsync($"""
            update safety.photo_review_cases set version={caseVersion}
            where id={caseId} and version={row.CaseVersion} and candidate_generation={generation}
            """, cancellationToken);
        var changedCandidate = await db.Database.ExecuteSqlAsync($"""
            update media.review_candidates set retention_until={maximum}
            where generation={generation} and case_id={caseId} and upload_id={photoId}
              and retention_until={row.RetentionUntil} and cleanup_requested_at is null and deleted_at is null
            """, cancellationToken);
        if (changedUpload != 1 || changedCase != 1 || changedCandidate != 1)
        {
            throw new InvalidOperationException("All guarded appeal projections must change exactly once.");
        }

        await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_appeal_commands(actor_id,key_hash,intent_sha256,appeal_id,photo_id,
              result_photo_version,result_requested_at,result_response_target_at,result_maximum_retention_until,created_at)
            values({owner.UserId},{keyHash},{intent},{appealId},{photoId},{photoVersion},{now},{target},{maximum},{now})
            """, cancellationToken);
        await new AuditTrail(db, _time).AppendAsync(new AuditRecord(AuditActorKind.User, owner.UserId,
            AuditAction.PhotoReviewAppealRequested, AuditTargetTypes.PhotoReviewAppeal, appealId, targetVersion: 1), cancellationToken);
        // Return PostgreSQL's canonical timestamp precision on both the first response and every replay.
        var confirmed = await ReplayAsync(db, owner.UserId, keyHash, intent, cancellationToken)
            as PhotoReviewAppealRequestResult.Accepted
            ?? throw new InvalidOperationException("An appeal request needs its persisted receipt.");
        await transaction.CommitAsync(cancellationToken);
        return new Phase(null, confirmed);
    }

    private async Task<bool> AuthorizeOwnerAsync(DuoraDbContext db, PhotoReviewActor owner, CancellationToken cancellationToken)
    {
        if (owner.UserId == Guid.Empty || owner.SessionId == Guid.Empty)
        {
            return false;
        }

        await db.Database.ExecuteSqlAsync($"set local lock_timeout='2s'", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.user_guards(user_id) select id from identity.users where id={owner.UserId} on conflict do nothing
            """, cancellationToken);
        var guards = await db.Database.SqlQuery<Guid>($"""
            select user_id as "Value" from safety.user_guards where user_id={owner.UserId} for update
            """).ToListAsync(cancellationToken);
        if (guards.Count != 1)
        {
            return false;
        }

        await db.Database.ExecuteSqlAsync($"select 1 from identity.users where id={owner.UserId} for share", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
            where ur.user_id={owner.UserId} order by ur.role_id for share of ur,r nowait
            """, cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            select 1 from identity.login_sessions where id={owner.SessionId} and user_id={owner.UserId} for share
            """, cancellationToken);
        var now = _time.GetUtcNow();
        return await db.Database.SqlQuery<bool>($"""
            select exists(select 1 from identity.users u join identity.login_sessions s on s.user_id=u.id
              where u.id={owner.UserId} and s.id={owner.SessionId}
                and u.email_confirmed and u.temporary_password_expires_at is null
                and u.status in ('PendingEligibility','Active','Restricted','Suspended')
                and exists(select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
                  where ur.user_id=u.id and r.name='participant')
                and not exists(select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
                  where ur.user_id=u.id and r.name<>'participant')
                and s.policy='Normal' and s.security_stamp=u.security_stamp and s.revoked_at is null
                and s.idle_expires_at>{now} and s.absolute_expires_at>{now}) as "Value"
            """).SingleAsync(cancellationToken);
    }

    private static async Task<PhotoReviewAppealRequestResult?> ReplayAsync(DuoraDbContext db, Guid actorId,
        byte[] keyHash, byte[] intent, CancellationToken cancellationToken)
    {
        var receipt = await db.Database.SqlQuery<Receipt>($"""
            select appeal_id as "AppealId",photo_id as "PhotoId",intent_sha256 as "Intent",result_photo_version as "PhotoVersion",
              result_requested_at as "RequestedAt",result_response_target_at as "ResponseTargetAt",
              result_maximum_retention_until as "MaximumRetentionUntil"
            from safety.photo_review_appeal_commands where actor_id={actorId} and key_hash={keyHash}
            """).SingleOrDefaultAsync(cancellationToken);
        return receipt is null ? null : CryptographicOperations.FixedTimeEquals(receipt.Intent, intent)
            ? new PhotoReviewAppealRequestResult.Accepted(receipt.AppealId, receipt.PhotoId, receipt.PhotoVersion,
                receipt.RequestedAt, receipt.ResponseTargetAt, receipt.MaximumRetentionUntil)
            : new PhotoReviewAppealRequestResult.KeyReused();
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

    private sealed record Phase(Evidence? Evidence, PhotoReviewAppealRequestResult? Result);
    private sealed record Discovered(Guid UploadId, Guid? CaseId, Guid? Generation);
    private sealed record RejectionState(Guid UploadId, long UploadVersion, Guid CaseId, long CaseVersion, Guid Generation,
        string PolicyVersion, Guid DecisionId, DateTimeOffset RejectedAt, DateTimeOffset RetentionUntil);
    private sealed record Variant(string Kind, long SizeBytes, byte[] Sha256);
    private sealed record Receipt(Guid AppealId, Guid PhotoId, byte[] Intent, long PhotoVersion, DateTimeOffset RequestedAt,
        DateTimeOffset ResponseTargetAt, DateTimeOffset MaximumRetentionUntil);
    private sealed record Evidence(RejectionState State, Variant[] Variants)
    {
        public override string ToString() => "PhotoReviewAppealEvidence";
    }
}
