using System.Data;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.Infrastructure.Safety;

internal enum PhotoReviewApprovalReason
{
    MeetsPolicy,
}

internal abstract record PhotoReviewApprovalResult
{
    internal sealed record Approved(Guid CaseId, long Version, long PhotoVersion, Guid AssetId) : PhotoReviewApprovalResult;
    internal sealed record Forbidden : PhotoReviewApprovalResult;
    internal sealed record NotFound : PhotoReviewApprovalResult;
    internal sealed record PreconditionFailed : PhotoReviewApprovalResult;
    internal sealed record KeyReused : PhotoReviewApprovalResult;
    internal sealed record StateConflict : PhotoReviewApprovalResult;
    internal sealed record Unavailable : PhotoReviewApprovalResult;
}

/// <summary>Unregistered initial human approval; private preparation never substitutes for live authority.</summary>
internal sealed class PhotoReviewApprovals
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;

    internal PhotoReviewApprovals(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _contexts = contexts;
        _store = store;
        _time = time;
    }

    internal async Task<PhotoReviewApprovalResult> ApproveAsync(PhotoReviewActor actor, Guid caseId, long expectedVersion,
        PhotoReviewApprovalReason reason, IdempotencyKey key, CancellationToken cancellationToken)
    {
        var reasonCode = reason switch
        {
            PhotoReviewApprovalReason.MeetsPolicy => "meets_policy",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown human approval reason."),
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
        var intent = SHA256.HashData(Encoding.UTF8.GetBytes($"approve\n{caseId:D}\n{reasonCode}"));
        var initial = await PhaseAsync(actor, caseId, expectedVersion, keyHash, intent, reasonCode,
            expected: null, promotion: null, cancellationToken);
        if (initial.Result is { } result)
        {
            return result;
        }

        var snapshot = initial.Evidence ?? throw new InvalidOperationException("A guarded candidate snapshot is required.");
        Guid? generation = null;
        try
        {
            await using (var media = await _contexts.CreateDbContextAsync(cancellationToken))
            {
                var state = snapshot.State;
                var request = new ReviewPromotionRequest(caseId, state.CaseVersion, state.UploadVersion,
                    state.Generation, state.PolicyVersion);
                if (await new PhotoReviewPromotions(media, _store, _time).PrepareAsync(request, cancellationToken)
                    is not ReviewPromotionResult.Prepared prepared)
                {
                    // A competing commit can invalidate preparation after the copies. Only a currently
                    // authorized replay of the exact durable receipt can turn that refusal into success.
                    return await ReplayAfterPreparationAsync(actor, keyHash, intent, cancellationToken);
                }

                generation = prepared.Generation;
            }

            var final = await PhaseAsync(actor, caseId, expectedVersion, keyHash, intent, reasonCode,
                snapshot, generation, cancellationToken);
            return final.Result ?? throw new InvalidOperationException("A human decision needs a confirmed result.");
        }
        finally
        {
            if (generation is { } owned)
            {
                await RetainCleanupAsync(owned);
            }
        }
    }

    private async Task RetainCleanupAsync(Guid generation)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await using var db = await _contexts.CreateDbContextAsync(cleanup.Token);
            var now = _time.GetUtcNow();
            await db.Database.ExecuteSqlAsync($"""
                update media.review_promotions set cleanup_requested_at=coalesce(cleanup_requested_at,{now}),
                  cleanup_available_at=least(cleanup_available_at,{now})
                where generation={generation} and committed_at is null and deleted_at is null
                """, cleanup.Token);
        }
        catch (Exception exception) when (exception is IOException or NpgsqlException or DbUpdateException
            or OperationCanceledException or InvalidOperationException)
        {
            // Confirmed initial metadata/deadline remains a durable obligation even if this best effort fails.
        }
    }

    private async Task<PhotoReviewApprovalResult> ReplayAfterPreparationAsync(PhotoReviewActor actor,
        byte[] keyHash, byte[] intent, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await new PhotoReviewAccess(db, _time).AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
        {
            return new PhotoReviewApprovalResult.Forbidden();
        }
        return await ReplayAsync(db, actor.UserId, keyHash, intent, cancellationToken)
            ?? new PhotoReviewApprovalResult.Unavailable();
    }

    private async Task<Phase> PhaseAsync(PhotoReviewActor actor, Guid caseId, long expectedVersion,
        byte[] keyHash, byte[] intent, string reason, Evidence? expected, Guid? promotion, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var access = new PhotoReviewAccess(db, _time);
        await using (var preauthorization = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
            {
                return new Phase(null, new PhotoReviewApprovalResult.Forbidden());
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
            return new Phase(null, new PhotoReviewApprovalResult.NotFound());
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var present = await GuardUsersAsync(db, [actor.UserId, discovered.OwnerId], cancellationToken);
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
        {
            return new Phase(null, new PhotoReviewApprovalResult.Forbidden());
        }
        if (await ReplayAsync(db, actor.UserId, keyHash, intent, cancellationToken) is { } confirmed)
        {
            return new Phase(null, confirmed);
        }
        if (!present)
        {
            return new Phase(null, new PhotoReviewApprovalResult.NotFound());
        }
        await LockBusinessRowsAsync(db, caseId, discovered, promotion is not null, cancellationToken);
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
        {
            return new Phase(null, new PhotoReviewApprovalResult.Forbidden());
        }
        var row = await db.Database.SqlQuery<CaseState>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",u.version as "UploadVersion",u.state as "State",
              r.candidate_generation as "Generation",r.version as "CaseVersion",r.policy_version as "PolicyVersion",
              r.review_deadline as "ReviewDeadline",c.retention_until as "RetentionUntil",c.upload_version as "PreparedVersion",
              c.detected_type as "DetectedType",c.width as "Width",c.height as "Height",encode(u.content_sha256,'hex') as "OriginalSha256"
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
            return new Phase(null, new PhotoReviewApprovalResult.NotFound());
        }
        if (row.State != "AwaitingReview")
        {
            return new Phase(null, new PhotoReviewApprovalResult.StateConflict());
        }
        if (row.CaseVersion != expectedVersion)
        {
            return new Phase(null, new PhotoReviewApprovalResult.PreconditionFailed());
        }
        var variants = await db.Database.SqlQuery<Variant>($"""
            select kind as "Kind",width as "Width",height as "Height",size_bytes as "SizeBytes",sha256 as "Sha256"
            from media.review_candidate_variants where generation={row.Generation}
              and object_key='review-candidates/' || replace({row.Generation}::text,'-','') || '-' || kind
            order by kind
            """).ToListAsync(cancellationToken);
        if (!ValidManifest(variants) || row.OriginalSha256.Length != 64
            || !PhotoFormats.TryParseContentType(row.DetectedType, out _))
        {
            return new Phase(null, new PhotoReviewApprovalResult.Unavailable());
        }
        var evidence = new Evidence(row, [.. variants]);
        if (expected is not null && !Matches(expected, evidence))
        {
            return new Phase(null, new PhotoReviewApprovalResult.StateConflict());
        }
        // Time and step-up are checked after every business lock and all metadata reads, again at the effect.
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
        {
            return new Phase(null, new PhotoReviewApprovalResult.Forbidden());
        }
        var now = _time.GetUtcNow();
        if (row.ReviewDeadline <= now || row.RetentionUntil <= now || row.UploadVersion != row.PreparedVersion + 1)
        {
            return new Phase(null, new PhotoReviewApprovalResult.StateConflict());
        }
        if (promotion is not { } generation)
        {
            await transaction.CommitAsync(cancellationToken);
            return new Phase(evidence, null);
        }

        await db.Database.ExecuteSqlAsync($"select 1 from media.review_promotions where generation={generation} for update", cancellationToken);
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
        {
            return new Phase(null, new PhotoReviewApprovalResult.Forbidden());
        }
        now = _time.GetUtcNow();
        var prepared = await db.Database.SqlQuery<PromotionState>($"""
            select prepare_deadline as "Deadline" from media.review_promotions
            where generation={generation} and case_id={caseId} and upload_id={row.UploadId}
              and case_version={row.CaseVersion} and upload_version={row.UploadVersion}
              and candidate_generation={row.Generation} and policy_version={row.PolicyVersion}
              and detected_type={row.DetectedType} and width={row.Width} and height={row.Height}
              and prepare_deadline>{now} and writer_settled_at is not null
              and committed_at is null and asset_id is null and cleanup_requested_at is null
              and cleanup_token is null and deleted_at is null
            """).SingleOrDefaultAsync(cancellationToken);
        if (prepared is null)
        {
            return new Phase(null, new PhotoReviewApprovalResult.Unavailable());
        }
        // Append-only manifests need SELECT, not FOR SHARE (which requires UPDATE privileges).
        var finalVariants = await db.Database.SqlQuery<Variant>($"""
            select kind as "Kind",width as "Width",height as "Height",size_bytes as "SizeBytes",sha256 as "Sha256"
            from media.review_promotion_variants where generation={generation}
              and object_key='derivatives/review-' || replace({generation}::text,'-','') || '-' || kind
            order by kind
            """).ToListAsync(cancellationToken);
        if (!ValidManifest(finalVariants) || !Matches(evidence, new Evidence(row, [.. finalVariants])))
        {
            return new Phase(null, new PhotoReviewApprovalResult.Unavailable());
        }
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
        {
            return new Phase(null, new PhotoReviewApprovalResult.Forbidden());
        }
        now = _time.GetUtcNow();
        if (prepared.Deadline <= now || row.ReviewDeadline <= now || row.RetentionUntil <= now)
        {
            return new Phase(null, new PhotoReviewApprovalResult.Unavailable());
        }

        var caseVersion = checked(row.CaseVersion + 1);
        var photoVersion = checked(row.UploadVersion + 1);
        var id = Guid.CreateVersion7(now);
        var candidateRetention = now.AddHours(24);
        var historyRetention = now.AddYears(1);
        if (!PhotoFormats.TryParseContentType(row.DetectedType, out var format))
        {
            return new Phase(null, new PhotoReviewApprovalResult.Unavailable());
        }
        var asset = MediaAsset.Approve(row.UploadId, row.OwnerId, format, row.Width, row.Height,
            Convert.FromHexString(row.OriginalSha256), now);
        db.Add(asset);
        foreach (var variant in finalVariants)
        {
            var kind = PhotoVariants.All.Single(value => PhotoVariants.Name(value) == variant.Kind);
            db.Add(MediaVariant.Create(asset.Id, kind, ObjectKey.ForPromotedDerivative(generation, kind),
                variant.Width, variant.Height, variant.SizeBytes, now));
        }
        await db.SaveChangesAsync(cancellationToken);
        var changedPromotion = await db.Database.ExecuteSqlAsync($"""
            update media.review_promotions set committed_at={now},asset_id={asset.Id}
            where generation={generation} and committed_at is null and asset_id is null
              and writer_settled_at is not null and prepare_deadline>{now}
              and cleanup_requested_at is null and cleanup_token is null and deleted_at is null
            """, cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_decisions(id,case_id,case_version,actor_id,decision,reason_code,
              candidate_generation,policy_version,decided_at,candidate_retention_until_snapshot,history_retain_until)
            values({id},{caseId},{caseVersion},{actor.UserId},'approve',{reason},{row.Generation},{row.PolicyVersion},
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
            update media.uploads set state='Approved',rejection_code=null,version={photoVersion}
            where id={row.UploadId} and state='AwaitingReview' and version={row.UploadVersion}
            """, cancellationToken);
        var changedCase = await db.Database.ExecuteSqlAsync($"""
            update safety.photo_review_cases set version={caseVersion} where id={caseId} and version={row.CaseVersion}
            """, cancellationToken);
        var changedCandidate = await db.Database.ExecuteSqlAsync($"""
            update media.review_candidates set retention_until={candidateRetention},cleanup_requested_at={now},
              cleanup_available_at=least(cleanup_available_at,{now})
            where generation={row.Generation} and case_id={caseId} and cleanup_requested_at is null and deleted_at is null
            """, cancellationToken);
        if (changedUpload != 1 || changedCase != 1 || changedCandidate != 1 || changedPromotion != 1)
        {
            throw new InvalidOperationException("All guarded human decision projections must change exactly once.");
        }
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_decision_commands(actor_id,operation,key_hash,case_id,intent_sha256,
              result_version,result_photo_version,created_at,result_decision,result_asset_id)
            values({actor.UserId},'decide',{keyHash},{caseId},{intent},{caseVersion},{photoVersion},{now},'approve',{asset.Id})
            """, cancellationToken);
        await new AuditTrail(db, _time).AppendAsync(new AuditRecord(AuditActorKind.Moderator, actor.UserId,
            AuditAction.PhotoReviewApproved, AuditTargetTypes.PhotoReviewDecision, id, targetVersion: caseVersion), cancellationToken);
        if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Decide, cancellationToken))
        {
            return new Phase(null, new PhotoReviewApprovalResult.Forbidden());
        }
        var finishedAt = _time.GetUtcNow();
        if (prepared.Deadline <= finishedAt || row.ReviewDeadline <= finishedAt || row.RetentionUntil <= finishedAt)
        {
            return new Phase(null, new PhotoReviewApprovalResult.Unavailable());
        }
        await transaction.CommitAsync(cancellationToken);
        return new Phase(null, new PhotoReviewApprovalResult.Approved(caseId, caseVersion, photoVersion, asset.Id));
    }

    private static bool ValidManifest(List<Variant> variants) => variants.Count == 3
        && variants.Select(v => v.Kind).SequenceEqual(PhotoVariants.All.Select(PhotoVariants.Name).Order())
        && variants.All(v => v.SizeBytes is > 0 and <= 16_777_216 && v.Sha256.Length == 32)
        && variants.Sum(v => v.SizeBytes) <= 25_165_824;

    private static bool Matches(Evidence expected, Evidence current) => expected.State == current.State
        && expected.Variants.Zip(current.Variants).All(pair => pair.First.Kind == pair.Second.Kind
            && pair.First.SizeBytes == pair.Second.SizeBytes && pair.First.Width == pair.Second.Width
            && pair.First.Height == pair.Second.Height
            && CryptographicOperations.FixedTimeEquals(pair.First.Sha256, pair.Second.Sha256));

    private static async Task<PhotoReviewApprovalResult?> ReplayAsync(DuoraDbContext db, Guid actorId,
        byte[] keyHash, byte[] intent, CancellationToken cancellationToken)
    {
        var receipt = await db.Database.SqlQuery<Receipt>($"""
            select case_id as "CaseId",intent_sha256 as "Intent",result_version as "CaseVersion",result_photo_version as "PhotoVersion",
              result_decision as "Decision",result_asset_id as "AssetId"
            from safety.photo_review_decision_commands where actor_id={actorId} and operation='decide' and key_hash={keyHash}
            """).SingleOrDefaultAsync(cancellationToken);
        if (receipt is null)
        {
            return null;
        }
        if (!CryptographicOperations.FixedTimeEquals(receipt.Intent, intent))
        {
            return new PhotoReviewApprovalResult.KeyReused();
        }
        return receipt.Decision == "approve" && receipt.AssetId is { } asset && asset != Guid.Empty
            ? new PhotoReviewApprovalResult.Approved(receipt.CaseId, receipt.CaseVersion, receipt.PhotoVersion, asset)
            : new PhotoReviewApprovalResult.Unavailable();
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

    private sealed record Phase(Evidence? Evidence, PhotoReviewApprovalResult? Result);
    private sealed record Discovered(Guid OwnerId, Guid UploadId, Guid Generation);
    private sealed record Receipt(Guid CaseId, byte[] Intent, long CaseVersion, long PhotoVersion, string Decision, Guid? AssetId);
    private sealed record PromotionState(DateTimeOffset Deadline);
    private sealed record Variant(string Kind, int Width, int Height, long SizeBytes, byte[] Sha256);
    private sealed record CaseState(Guid OwnerId, Guid UploadId, long UploadVersion, string State, Guid Generation,
        long CaseVersion, string PolicyVersion, DateTimeOffset ReviewDeadline, DateTimeOffset RetentionUntil, long PreparedVersion,
        string DetectedType, int Width, int Height, string OriginalSha256);
    private sealed record Evidence(CaseState State, Variant[] Variants)
    {
        public override string ToString() => "PhotoReviewDecisionEvidence";
    }
}
