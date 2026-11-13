using System.Data;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
        var request = new DecisionRequest(actor, caseId, expectedVersion, keyHash, intent, reasonCode);
        var initial = await PhaseAsync(request, expected: null, promotion: null, cancellationToken);
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
                var promotionRequest = new ReviewPromotionRequest(caseId, state.CaseVersion, state.UploadVersion,
                    state.Generation, state.PolicyVersion);
                if (await new PhotoReviewPromotions(media, _store, _time).PrepareAsync(promotionRequest, cancellationToken)
                    is not ReviewPromotionResult.Prepared prepared)
                {
                    // Without a prepared generation this phase can only observe an authorized receipt
                    // or current business state; it cannot commit a new approval.
                    var observed = await PhaseAsync(request, snapshot, promotion: null, cancellationToken);
                    var outcome = observed.Result;
                    return outcome is PhotoReviewApprovalResult.Approved or PhotoReviewApprovalResult.Forbidden
                        or PhotoReviewApprovalResult.KeyReused or PhotoReviewApprovalResult.StateConflict
                        ? outcome : new PhotoReviewApprovalResult.Unavailable();
                }

                generation = prepared.Generation;
            }

            var final = await PhaseAsync(request, snapshot, generation, cancellationToken);
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

    /// <summary>
    /// One human-decision phase. <paramref name="expected"/> is null in the initial phase and the guarded snapshot
    /// afterwards; <paramref name="promotion"/> is the prepared generation only in the committing phase.
    /// Every early return leaves its transaction uncommitted, so disposal rolls it back.
    /// </summary>
    private async Task<Phase> PhaseAsync(DecisionRequest request, Evidence? expected, Guid? promotion,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var phase = new PhaseContext(db, new PhotoReviewAccess(db, _time), request);
        if (await RejectBeforeDiscoveryAsync(phase, cancellationToken) is { } refused)
        {
            return new Phase(null, refused);
        }
        if (await DiscoverAsync(phase, cancellationToken) is not { } discovered)
        {
            return new Phase(null, new PhotoReviewApprovalResult.NotFound());
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (await RejectUnlessLockedAndAuthorizedAsync(phase, discovered, writing: promotion is not null, cancellationToken)
            is { } blocked)
        {
            return new Phase(null, blocked);
        }
        var loaded = await LoadCandidateAsync(phase, discovered, expected, cancellationToken);
        if (loaded.Value is not { } candidate)
        {
            return new Phase(null, loaded.Rejection);
        }
        if (promotion is not { } generation)
        {
            return await CompleteWithoutPromotionAsync(transaction, candidate.Evidence, expected, cancellationToken);
        }

        return await PromoteAsync(phase, transaction, candidate, generation, cancellationToken);
    }

    private static async Task<PhotoReviewApprovalResult?> RejectBeforeDiscoveryAsync(PhaseContext phase,
        CancellationToken cancellationToken)
    {
        await using var preauthorization = await phase.Db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (await phase.DenyUnlessAuthorizedAsync(cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        return await ReplayAsync(phase.Db, phase.Request, cancellationToken);
    }

    private static Task<Discovered?> DiscoverAsync(PhaseContext phase, CancellationToken cancellationToken) =>
        phase.Db.Database.SqlQuery<Discovered>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",r.candidate_generation as "Generation"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id where r.id={phase.Request.CaseId}
            """).SingleOrDefaultAsync(cancellationToken);

    private static async Task<PhotoReviewApprovalResult?> RejectUnlessLockedAndAuthorizedAsync(PhaseContext phase,
        Discovered discovered, bool writing, CancellationToken cancellationToken)
    {
        var request = phase.Request;
        var present = await GuardUsersAsync(phase.Db, [request.Actor.UserId, discovered.OwnerId], cancellationToken);
        if (await phase.DenyUnlessAuthorizedAsync(cancellationToken) is { } forbidden)
        {
            return forbidden;
        }
        if (await ReplayAsync(phase.Db, request, cancellationToken) is { } confirmed)
        {
            return confirmed;
        }
        if (!present)
        {
            return new PhotoReviewApprovalResult.NotFound();
        }

        await LockBusinessRowsAsync(phase.Db, request.CaseId, discovered, writing, cancellationToken);
        return await phase.DenyUnlessAuthorizedAsync(cancellationToken);
    }

    private async Task<Gate<Candidate>> LoadCandidateAsync(PhaseContext phase, Discovered discovered,
        Evidence? expected, CancellationToken cancellationToken)
    {
        var current = await LoadCaseStateAsync(phase, discovered, cancellationToken);
        if (current.Value is not { } row)
        {
            return current.Propagate<Candidate>();
        }

        var loaded = await LoadEvidenceAsync(phase.Db, row, expected, cancellationToken);
        if (loaded.Value is null)
        {
            return loaded;
        }
        if (await RejectUnlessAuthorizedAndFreshAsync(phase, row, cancellationToken) is { } stale)
        {
            return Gate<Candidate>.Reject(stale);
        }

        return loaded;
    }

    private static async Task<Gate<CaseState>> LoadCaseStateAsync(PhaseContext phase, Discovered discovered,
        CancellationToken cancellationToken)
    {
        var request = phase.Request;
        var row = await phase.Db.Database.SqlQuery<CaseState>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",u.version as "UploadVersion",u.state as "State",
              r.candidate_generation as "Generation",r.version as "CaseVersion",r.policy_version as "PolicyVersion",
              r.review_deadline as "ReviewDeadline",c.retention_until as "RetentionUntil",c.upload_version as "PreparedVersion",
              c.detected_type as "DetectedType",c.width as "Width",c.height as "Height",encode(u.content_sha256,'hex') as "OriginalSha256"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id
            join identity.users owner on owner.id=u.owner_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.upload_id=u.id and c.case_id=r.id
            where r.id={request.CaseId} and u.id={discovered.UploadId} and u.owner_id={discovered.OwnerId}
              and r.candidate_generation={discovered.Generation}
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active')
              and (u.state<>'AwaitingReview' or (c.committed_at is not null and c.writer_settled_at is not null
                and c.cleanup_requested_at is null and c.deleted_at is null))
              and (select a.reviewer_id from safety.photo_review_assignments a where a.case_id=r.id
                order by a.case_version desc limit 1)={request.Actor.UserId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (row is null || row.State == "Deleted")
        {
            return Gate<CaseState>.Reject(new PhotoReviewApprovalResult.NotFound());
        }
        if (row.State != "AwaitingReview")
        {
            return Gate<CaseState>.Reject(new PhotoReviewApprovalResult.StateConflict());
        }
        if (row.CaseVersion != request.ExpectedVersion)
        {
            return Gate<CaseState>.Reject(new PhotoReviewApprovalResult.PreconditionFailed());
        }

        return Gate<CaseState>.Pass(row);
    }

    private static async Task<Gate<Candidate>> LoadEvidenceAsync(DuoraDbContext db, CaseState row,
        Evidence? expected, CancellationToken cancellationToken)
    {
        var variants = await db.Database.SqlQuery<Variant>($"""
            select kind as "Kind",width as "Width",height as "Height",size_bytes as "SizeBytes",sha256 as "Sha256"
            from media.review_candidate_variants where generation={row.Generation}
              and object_key='review-candidates/' || replace({row.Generation}::text,'-','') || '-' || kind
            order by kind
            """).ToListAsync(cancellationToken);
        if (!ValidManifest(variants) || row.OriginalSha256.Length != 64
            || !PhotoFormats.TryParseContentType(row.DetectedType, out _))
        {
            return Gate<Candidate>.Reject(new PhotoReviewApprovalResult.Unavailable());
        }

        var evidence = new Evidence(row, [.. variants]);
        if (expected is not null && !Matches(expected, evidence))
        {
            return Gate<Candidate>.Reject(new PhotoReviewApprovalResult.StateConflict());
        }

        return Gate<Candidate>.Pass(new Candidate(row, variants, evidence));
    }

    private async Task<PhotoReviewApprovalResult?> RejectUnlessAuthorizedAndFreshAsync(PhaseContext phase,
        CaseState row, CancellationToken cancellationToken)
    {
        // Time and step-up are checked after every business lock and all metadata reads, again at the effect.
        if (await phase.DenyUnlessAuthorizedAsync(cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        return IsStaleAt(row, _time.GetUtcNow()) ? new PhotoReviewApprovalResult.StateConflict() : null;
    }

    private static bool IsStaleAt(CaseState row, DateTimeOffset now) => row.ReviewDeadline <= now
        || row.RetentionUntil <= now || row.UploadVersion != row.PreparedVersion + 1;

    private static async Task<Phase> CompleteWithoutPromotionAsync(IDbContextTransaction transaction,
        Evidence evidence, Evidence? expected, CancellationToken cancellationToken)
    {
        // Only the initial phase may persist guard setup. A post-preparation observation has
        // an expected snapshot and leaves even guard inserts uncommitted on disposal.
        if (expected is null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new Phase(evidence, null);
    }

    private async Task<Phase> PromoteAsync(PhaseContext phase, IDbContextTransaction transaction, Candidate candidate,
        Guid generation, CancellationToken cancellationToken)
    {
        var loaded = await LoadPromotableAsync(phase, candidate, generation, cancellationToken);
        if (loaded.Value is not { } promotable)
        {
            return new Phase(null, loaded.Rejection);
        }

        return await CommitApprovalAsync(phase, transaction, candidate, promotable, generation, cancellationToken);
    }

    private async Task<Gate<Promotable>> LoadPromotableAsync(PhaseContext phase, Candidate candidate,
        Guid generation, CancellationToken cancellationToken)
    {
        var loaded = await ReadPreparedPromotionAsync(phase, candidate.Row, generation, cancellationToken);
        if (loaded.Value is not { } prepared)
        {
            return loaded.Propagate<Promotable>();
        }

        return await ReadFinalManifestAsync(phase, candidate, prepared, generation, cancellationToken);
    }

    private async Task<Gate<PromotionState>> ReadPreparedPromotionAsync(PhaseContext phase, CaseState row,
        Guid generation, CancellationToken cancellationToken)
    {
        var db = phase.Db;
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_promotions where generation={generation} for update", cancellationToken);
        if (await phase.DenyUnlessAuthorizedAsync(cancellationToken) is { } forbidden)
        {
            return Gate<PromotionState>.Reject(forbidden);
        }

        var now = _time.GetUtcNow();
        var prepared = await db.Database.SqlQuery<PromotionState>($"""
            select prepare_deadline as "Deadline" from media.review_promotions
            where generation={generation} and case_id={phase.Request.CaseId} and upload_id={row.UploadId}
              and case_version={row.CaseVersion} and upload_version={row.UploadVersion}
              and candidate_generation={row.Generation} and policy_version={row.PolicyVersion}
              and detected_type={row.DetectedType} and width={row.Width} and height={row.Height}
              and prepare_deadline>{now} and writer_settled_at is not null
              and committed_at is null and asset_id is null and cleanup_requested_at is null
              and cleanup_token is null and deleted_at is null
            """).SingleOrDefaultAsync(cancellationToken);
        return prepared is null
            ? Gate<PromotionState>.Reject(new PhotoReviewApprovalResult.Unavailable())
            : Gate<PromotionState>.Pass(prepared);
    }

    private async Task<Gate<Promotable>> ReadFinalManifestAsync(PhaseContext phase, Candidate candidate,
        PromotionState prepared, Guid generation, CancellationToken cancellationToken)
    {
        // Append-only manifests need SELECT, not FOR SHARE (which requires UPDATE privileges).
        var finalVariants = await phase.Db.Database.SqlQuery<Variant>($"""
            select kind as "Kind",width as "Width",height as "Height",size_bytes as "SizeBytes",sha256 as "Sha256"
            from media.review_promotion_variants where generation={generation}
              and object_key='derivatives/review-' || replace({generation}::text,'-','') || '-' || kind
            order by kind
            """).ToListAsync(cancellationToken);
        if (!ValidManifest(finalVariants) || !Matches(candidate.Evidence, new Evidence(candidate.Row, [.. finalVariants])))
        {
            return Gate<Promotable>.Reject(new PhotoReviewApprovalResult.Unavailable());
        }
        if (await phase.DenyUnlessAuthorizedAsync(cancellationToken) is { } forbidden)
        {
            return Gate<Promotable>.Reject(forbidden);
        }

        var now = _time.GetUtcNow();
        if (DeadlinePassed(candidate.Row, prepared, now))
        {
            return Gate<Promotable>.Reject(new PhotoReviewApprovalResult.Unavailable());
        }

        return Gate<Promotable>.Pass(new Promotable(prepared, finalVariants, now));
    }

    private async Task<Phase> CommitApprovalAsync(PhaseContext phase, IDbContextTransaction transaction,
        Candidate candidate, Promotable promotable, Guid generation, CancellationToken cancellationToken)
    {
        var db = phase.Db;
        var request = phase.Request;
        var row = candidate.Row;
        var approval = Approval.Plan(row, promotable.Now);
        if (!PhotoFormats.TryParseContentType(row.DetectedType, out var format))
        {
            return new Phase(null, new PhotoReviewApprovalResult.Unavailable());
        }

        var asset = await InsertApprovedAssetAsync(db, row, format, promotable, generation, cancellationToken);
        var changedPromotion = await MarkPromotionCommittedAsync(db, generation, asset.Id, approval.Now, cancellationToken);
        await InsertDecisionAsync(db, request, candidate, approval, cancellationToken);
        await ProjectApprovalAsync(db, request.CaseId, row, approval, changedPromotion, cancellationToken);
        await InsertReceiptAsync(db, request, approval, asset.Id, cancellationToken);
        await AppendAuditAsync(db, request.Actor.UserId, approval, cancellationToken);
        if (await RejectAtCommitAsync(phase, row, promotable.Prepared, cancellationToken) is { } rejected)
        {
            return new Phase(null, rejected);
        }

        await transaction.CommitAsync(cancellationToken);
        return new Phase(null, new PhotoReviewApprovalResult.Approved(request.CaseId, approval.CaseVersion, approval.PhotoVersion, asset.Id));
    }

    private static async Task<MediaAsset> InsertApprovedAssetAsync(DuoraDbContext db, CaseState row, PhotoFormat format,
        Promotable promotable, Guid generation, CancellationToken cancellationToken)
    {
        var now = promotable.Now;
        var asset = MediaAsset.Approve(row.UploadId, row.OwnerId, format, row.Width, row.Height,
            Convert.FromHexString(row.OriginalSha256), now);
        db.Add(asset);
        foreach (var variant in promotable.Variants)
        {
            var kind = PhotoVariants.All.Single(value => PhotoVariants.Name(value) == variant.Kind);
            db.Add(MediaVariant.Create(asset.Id, kind, ObjectKey.ForPromotedDerivative(generation, kind),
                variant.Width, variant.Height, variant.SizeBytes, now));
        }
        await db.SaveChangesAsync(cancellationToken);
        return asset;
    }

    private static Task<int> MarkPromotionCommittedAsync(DuoraDbContext db, Guid generation, Guid assetId,
        DateTimeOffset now, CancellationToken cancellationToken) => db.Database.ExecuteSqlAsync($"""
            update media.review_promotions set committed_at={now},asset_id={assetId}
            where generation={generation} and committed_at is null and asset_id is null
              and writer_settled_at is not null and prepare_deadline>{now}
              and cleanup_requested_at is null and cleanup_token is null and deleted_at is null
            """, cancellationToken);

    private static async Task InsertDecisionAsync(DuoraDbContext db, DecisionRequest request, Candidate candidate,
        Approval approval, CancellationToken cancellationToken)
    {
        var row = candidate.Row;
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_decisions(id,case_id,case_version,actor_id,decision,reason_code,
              candidate_generation,policy_version,decided_at,candidate_retention_until_snapshot,history_retain_until)
            values({approval.DecisionId},{request.CaseId},{approval.CaseVersion},{request.Actor.UserId},'approve',{request.Reason},
              {row.Generation},{row.PolicyVersion},{approval.Now},{approval.CandidateRetention},{approval.HistoryRetention})
            """, cancellationToken);
        foreach (var variant in candidate.Variants)
        {
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.photo_review_decision_variants(decision_id,variant,size_bytes,sha256)
                values({approval.DecisionId},{variant.Kind},{variant.SizeBytes},{variant.Sha256})
                """, cancellationToken);
        }
    }

    private static async Task ProjectApprovalAsync(DuoraDbContext db, Guid caseId, CaseState row, Approval approval,
        int changedPromotion, CancellationToken cancellationToken)
    {
        var changedUpload = await db.Database.ExecuteSqlAsync($"""
            update media.uploads set state='Approved',rejection_code=null,version={approval.PhotoVersion}
            where id={row.UploadId} and state='AwaitingReview' and version={row.UploadVersion}
            """, cancellationToken);
        var changedCase = await db.Database.ExecuteSqlAsync($"""
            update safety.photo_review_cases set version={approval.CaseVersion} where id={caseId} and version={row.CaseVersion}
            """, cancellationToken);
        var changedCandidate = await db.Database.ExecuteSqlAsync($"""
            update media.review_candidates set retention_until={approval.CandidateRetention},cleanup_requested_at={approval.Now},
              cleanup_available_at=least(cleanup_available_at,{approval.Now})
            where generation={row.Generation} and case_id={caseId} and cleanup_requested_at is null and deleted_at is null
            """, cancellationToken);
        if (changedUpload != 1 || changedCase != 1 || changedCandidate != 1 || changedPromotion != 1)
        {
            throw new InvalidOperationException("All guarded human decision projections must change exactly once.");
        }
    }

    private static Task<int> InsertReceiptAsync(DuoraDbContext db, DecisionRequest request, Approval approval,
        Guid assetId, CancellationToken cancellationToken) => db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_decision_commands(actor_id,operation,key_hash,case_id,intent_sha256,
              result_version,result_photo_version,created_at,result_decision,result_asset_id)
            values({request.Actor.UserId},'decide',{request.KeyHash},{request.CaseId},{request.Intent},{approval.CaseVersion},
              {approval.PhotoVersion},{approval.Now},'approve',{assetId})
            """, cancellationToken);

    private Task AppendAuditAsync(DuoraDbContext db, Guid actorId, Approval approval, CancellationToken cancellationToken) =>
        new AuditTrail(db, _time).AppendAsync(new AuditRecord(AuditActorKind.Moderator, actorId,
            AuditAction.PhotoReviewApproved, AuditTargetTypes.PhotoReviewDecision, approval.DecisionId,
            targetVersion: approval.CaseVersion), cancellationToken);

    private async Task<PhotoReviewApprovalResult?> RejectAtCommitAsync(PhaseContext phase, CaseState row,
        PromotionState prepared, CancellationToken cancellationToken)
    {
        if (await phase.DenyUnlessAuthorizedAsync(cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        return DeadlinePassed(row, prepared, _time.GetUtcNow()) ? new PhotoReviewApprovalResult.Unavailable() : null;
    }

    private static bool DeadlinePassed(CaseState row, PromotionState prepared, DateTimeOffset at) =>
        prepared.Deadline <= at || row.ReviewDeadline <= at || row.RetentionUntil <= at;

    private static bool ValidManifest(List<Variant> variants) => variants.Count == 3
        && variants.Select(v => v.Kind).SequenceEqual(PhotoVariants.All.Select(PhotoVariants.Name).Order())
        && variants.All(v => v.SizeBytes is > 0 and <= 16_777_216 && v.Sha256.Length == 32)
        && variants.Sum(v => v.SizeBytes) <= 25_165_824;

    private static bool Matches(Evidence expected, Evidence current) => expected.State == current.State
        && expected.Variants.Zip(current.Variants).All(pair => pair.First.Kind == pair.Second.Kind
            && pair.First.SizeBytes == pair.Second.SizeBytes && pair.First.Width == pair.Second.Width
            && pair.First.Height == pair.Second.Height
            && CryptographicOperations.FixedTimeEquals(pair.First.Sha256, pair.Second.Sha256));

    private static async Task<PhotoReviewApprovalResult?> ReplayAsync(DuoraDbContext db, DecisionRequest request,
        CancellationToken cancellationToken)
    {
        var receipt = await db.Database.SqlQuery<Receipt>($"""
            select case_id as "CaseId",intent_sha256 as "Intent",result_version as "CaseVersion",result_photo_version as "PhotoVersion",
              result_decision as "Decision",result_asset_id as "AssetId"
            from safety.photo_review_decision_commands where actor_id={request.Actor.UserId} and operation='decide' and key_hash={request.KeyHash}
            """).SingleOrDefaultAsync(cancellationToken);
        if (receipt is null)
        {
            return null;
        }
        if (!CryptographicOperations.FixedTimeEquals(receipt.Intent, request.Intent))
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

    private sealed record DecisionRequest(PhotoReviewActor Actor, Guid CaseId, long ExpectedVersion,
        byte[] KeyHash, byte[] Intent, string Reason);

    /// <summary>The phase's context plus the live-authority check every step repeats.</summary>
    private sealed class PhaseContext(DuoraDbContext db, PhotoReviewAccess access, DecisionRequest request)
    {
        internal DuoraDbContext Db => db;
        internal DecisionRequest Request => request;

        /// <summary>Null while the actor may still decide; otherwise the Forbidden answer.</summary>
        internal async Task<PhotoReviewApprovalResult?> DenyUnlessAuthorizedAsync(CancellationToken cancellationToken) =>
            await access.AuthorizeAsync(request.Actor, PhotoReviewCapability.Decide, cancellationToken)
                ? null : new PhotoReviewApprovalResult.Forbidden();
    }

    /// <summary>A step's value, or the answer that ends the phase early.</summary>
    private readonly record struct Gate<T>(T? Value, PhotoReviewApprovalResult? Rejection) where T : class
    {
        internal static Gate<T> Pass(T value) => new(value, null);
        internal static Gate<T> Reject(PhotoReviewApprovalResult rejection) => new(null, rejection);
        internal Gate<TOther> Propagate<TOther>() where TOther : class => new(null, Rejection);
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

    private sealed record Candidate(CaseState Row, List<Variant> Variants, Evidence Evidence);
    private sealed record Promotable(PromotionState Prepared, List<Variant> Variants, DateTimeOffset Now);

    private sealed record Approval(long CaseVersion, long PhotoVersion, Guid DecisionId, DateTimeOffset Now,
        DateTimeOffset CandidateRetention, DateTimeOffset HistoryRetention)
    {
        internal static Approval Plan(CaseState row, DateTimeOffset now) => new(checked(row.CaseVersion + 1),
            checked(row.UploadVersion + 1), Guid.CreateVersion7(now), now, now.AddHours(24), now.AddYears(1));
    }
}
