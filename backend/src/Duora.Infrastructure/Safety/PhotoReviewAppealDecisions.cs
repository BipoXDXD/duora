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

internal abstract record PhotoReviewAppealDecisionResult
{
    internal sealed record Decided(Guid AppealId, Guid CaseId, Guid DecisionId, long AppealVersion,
        long CaseVersion, long PhotoVersion, string Decision, Guid? AssetId) : PhotoReviewAppealDecisionResult;
    internal sealed record Forbidden : PhotoReviewAppealDecisionResult;
    internal sealed record NotFound : PhotoReviewAppealDecisionResult;
    internal sealed record PreconditionFailed : PhotoReviewAppealDecisionResult;
    internal sealed record KeyReused : PhotoReviewAppealDecisionResult;
    internal sealed record StateConflict : PhotoReviewAppealDecisionResult;
    internal sealed record Unavailable : PhotoReviewAppealDecisionResult;
}

/// <summary>Independent human appeal decision; owns every authorization phase and its confirmed receipt.</summary>
internal sealed class PhotoReviewAppealDecisions(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
{
    internal async Task<PhotoReviewAppealDecisionResult> DecideAsync(PhotoReviewActor actor, Guid appealId,
        long expectedVersion, bool approve, IdempotencyKey key, CancellationToken ct)
    {
        if (System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Appeal decisions own complete READ COMMITTED phases.");
        }
        if (!IdempotencyKey.TryParse(key.Value, out _))
        {
            throw new ArgumentException("A validated idempotency key is required.", nameof(key));
        }
        var decision = approve ? "approve" : "reject";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value));
        var intent = SHA256.HashData(Encoding.UTF8.GetBytes($"decide-photo-appeal\n{appealId:D}\n{decision}"));
        var first = await PhaseAsync(actor, appealId, expectedVersion, hash, intent, approve, null, null, false, ct);
        if (first.Result is { } refusal)
        {
            if (refusal is PhotoReviewAppealDecisionResult.StateConflict)
            {
                await new PhotoReviewAppealAvailability(contexts, store, time).ReconcileAsync(appealId, ct);
            }
            return refusal;
        }
        var evidence = first.Evidence ?? throw new InvalidOperationException("An appeal requires a guarded original rejection.");
        Guid? promotion = null;
        try
        {
            if (approve)
            {
                await using var db = await contexts.CreateDbContextAsync(ct);
                var state = evidence.State;
                var request = new ReviewAppealPromotionRequest(appealId, state.AppealVersion, state.OriginalDecisionId,
                    actor.UserId, new ReviewPromotionRequest(state.CaseId, state.CaseVersion, state.UploadVersion,
                        state.Generation, state.PolicyVersion));
                if (await new PhotoReviewPromotions(db, store, time).PrepareAppealAsync(request, ct)
                    is not ReviewPromotionResult.Prepared prepared)
                {
                    return new PhotoReviewAppealDecisionResult.Unavailable();
                }
                promotion = prepared.Generation;
            }
            else if (!await VerifyRejectionBytesAsync(actor, appealId, ct))
            {
                await new PhotoReviewAppealAvailability(contexts, store, time).ReconcileAsync(appealId, ct);
                return new PhotoReviewAppealDecisionResult.Unavailable();
            }
            var final = await PhaseAsync(actor, appealId, expectedVersion, hash, intent, approve, evidence, promotion, true, ct);
            return final.Result ?? throw new InvalidOperationException("An appeal decision requires a confirmed result.");
        }
        finally
        {
            if (promotion is { } generation)
            {
                await RetainCleanupAsync(generation);
            }
        }
    }

    private async Task<bool> VerifyRejectionBytesAsync(PhotoReviewActor actor, Guid appealId, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            foreach (var variant in PhotoVariants.All)
            {
                if (await new PhotoReviewAppealEvidence(contexts, store, time).OpenAsync(actor, appealId, variant, ct)
                    is not PhotoReviewEvidenceResult.Opened opened)
                {
                    return false;
                }
                await using var lease = opened.Lease;
                while (await lease.ReadAsync(buffer, ct) != 0)
                {
                    CryptographicOperations.ZeroMemory(buffer);
                }
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private async Task<Phase> PhaseAsync(PhotoReviewActor actor, Guid appealId, long expectedVersion,
        byte[] hash, byte[] intent, bool approve, Evidence? expected, Guid? promotion, bool commit, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var access = new PhotoReviewAccess(db, time);
        await using (var preauthorization = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            if (!await AuthorizeAsync(access, actor, ct))
            {
                return new(null, new PhotoReviewAppealDecisionResult.Forbidden());
            }
            if (await ReplayAsync(db, actor.UserId, hash, intent, ct) is { } replay)
            {
                return new(null, replay);
            }
        }
        var discovered = await db.Database.SqlQuery<Discovered>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",r.id as "CaseId",r.candidate_generation as "Generation"
            from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id where a.id={appealId}
            """).SingleOrDefaultAsync(ct);
        if (discovered is null)
        {
            return new(null, new PhotoReviewAppealDecisionResult.NotFound());
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var present = await GuardUsersAsync(db, [actor.UserId, discovered.OwnerId], ct);
        if (!await AuthorizeAsync(access, actor, ct))
        {
            return new(null, new PhotoReviewAppealDecisionResult.Forbidden());
        }
        if (await ReplayAsync(db, actor.UserId, hash, intent, ct) is { } confirmed)
        {
            return new(null, confirmed);
        }
        if (!present)
        {
            return new(null, new PhotoReviewAppealDecisionResult.NotFound());
        }
        // Same global order as assignment/evidence, with exclusive business locks for a conditional effect.
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={discovered.UploadId} for update", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={discovered.CaseId} for update", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={discovered.Generation} for update", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidate_variants where generation={discovered.Generation} order by kind for share", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_appeals where id={appealId} for update", ct);
        if (!await AuthorizeAsync(access, actor, ct))
        {
            return new(null, new PhotoReviewAppealDecisionResult.Forbidden());
        }
        var row = await db.Database.SqlQuery<AppealState>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",u.version as "UploadVersion",r.id as "CaseId",
              r.version as "CaseVersion",r.candidate_generation as "Generation",r.policy_version as "PolicyVersion",
              a.version as "AppealVersion",a.original_decision_id as "OriginalDecisionId",
              a.maximum_retention_until as "Maximum",a.requested_at as "RequestedAt",c.detected_type as "DetectedType",
              c.width as "Width",c.height as "Height",encode(u.content_sha256,'hex') as "OriginalSha256"
            from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id join identity.users owner on owner.id=u.owner_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            join safety.photo_review_decisions d on d.id=a.original_decision_id and d.case_id=r.id
              and d.candidate_generation=c.generation and d.policy_version=r.policy_version
            join safety.photo_review_appeal_assignments s on s.appeal_id=a.id and s.appeal_version=a.version
            where a.id={appealId} and r.id={discovered.CaseId} and u.id={discovered.UploadId}
              and u.owner_id={discovered.OwnerId} and r.candidate_generation={discovered.Generation}
              and a.owner_id=u.owner_id and a.candidate_generation=c.generation and a.policy_version=r.policy_version
              and s.reviewer_id={actor.UserId} and d.actor_id<>{actor.UserId}
              and a.status='Open' and a.closed_at is null and a.version>=2 and a.version<9223372036854775807
              and r.version<9223372036854775807 and u.version<9223372036854775807
              and r.policy_version='human-review-v1' and d.appeal_id is null
              and d.decision='reject' and d.reason_code='does_not_meet_policy' and d.case_version=r.version-1
              and a.original_rejected_at=d.decided_at and d.candidate_retention_until_snapshot=d.decided_at+interval '168 hours'
              and a.requested_at>=d.decided_at and a.requested_at<d.decided_at+interval '168 hours'
              and a.response_target_at=a.requested_at+interval '168 hours'
              and a.maximum_retention_until=d.decided_at+interval '720 hours'
              and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_REJECTED' and c.upload_version=u.version-3
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active','Restricted','Suspended')
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.retention_until=a.maximum_retention_until and c.cleanup_requested_at is null
              and c.cleanup_audit_id is null and c.deleted_at is null and c.cleanup_token is null and c.cleanup_lease_until is null
            """).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return new(null, new PhotoReviewAppealDecisionResult.NotFound());
        }
        var instant = time.GetUtcNow();
        if (instant < row.RequestedAt || instant >= row.Maximum)
        {
            return new(null, new PhotoReviewAppealDecisionResult.StateConflict());
        }
        if (row.AppealVersion != expectedVersion)
        {
            return new(null, new PhotoReviewAppealDecisionResult.PreconditionFailed());
        }
        var variants = await db.Database.SqlQuery<Variant>($"""
            select v.kind as "Kind",v.width as "Width",v.height as "Height",v.size_bytes as "SizeBytes",v.sha256 as "Sha256"
            from media.review_candidate_variants v join safety.photo_review_decision_variants h
              on h.decision_id={row.OriginalDecisionId} and h.variant=v.kind and h.size_bytes=v.size_bytes and h.sha256=v.sha256
            where v.generation={row.Generation}
              and v.object_key='review-candidates/' || replace({row.Generation}::text,'-','') || '-' || v.kind
            order by v.kind
            """).ToListAsync(ct);
        if (!ValidManifest(variants, row) || row.OriginalSha256.Length != 64)
        {
            return new(null, new PhotoReviewAppealDecisionResult.Unavailable());
        }
        var evidence = new Evidence(row, [.. variants]);
        if (expected is not null && !Matches(expected, evidence))
        {
            return new(null, new PhotoReviewAppealDecisionResult.StateConflict());
        }
        if (!commit)
        {
            await transaction.CommitAsync(ct);
            return new(evidence, null);
        }
        List<Variant>? finalVariants = null;
        DateTimeOffset? promotionDeadline = null;
        if (approve)
        {
            if (promotion is not { } generation)
            {
                return new(null, new PhotoReviewAppealDecisionResult.Unavailable());
            }
            await db.Database.ExecuteSqlAsync($"select 1 from media.review_promotions where generation={generation} for update", ct);
            var prepared = await db.Database.SqlQuery<PromotionState>($"""
                select prepare_deadline as "Deadline" from media.review_promotions
                where generation={generation} and case_id={row.CaseId} and upload_id={row.UploadId}
                  and case_version={row.CaseVersion} and upload_version={row.UploadVersion}
                  and candidate_generation={row.Generation} and policy_version={row.PolicyVersion}
                  and appeal_id={appealId} and appeal_version={row.AppealVersion}
                  and detected_type={row.DetectedType} and width={row.Width} and height={row.Height}
                  and writer_settled_at is not null and committed_at is null and asset_id is null
                  and cleanup_requested_at is null and cleanup_token is null and deleted_at is null
                """).SingleOrDefaultAsync(ct);
            if (prepared is null)
            {
                return new(null, new PhotoReviewAppealDecisionResult.Unavailable());
            }
            promotionDeadline = prepared.Deadline;
            finalVariants = await db.Database.SqlQuery<Variant>($"""
                select kind as "Kind",width as "Width",height as "Height",size_bytes as "SizeBytes",sha256 as "Sha256"
                from media.review_promotion_variants where generation={generation}
                  and object_key='derivatives/review-' || replace({generation}::text,'-','') || '-' || kind order by kind
                """).ToListAsync(ct);
            if (!ValidManifest(finalVariants, row) || !Matches(evidence, new(row, [.. finalVariants])))
            {
                return new(null, new PhotoReviewAppealDecisionResult.Unavailable());
            }
        }
        if (!await AuthorizeAsync(access, actor, ct))
        {
            return new(null, new PhotoReviewAppealDecisionResult.Forbidden());
        }
        instant = time.GetUtcNow();
        if (instant < row.RequestedAt || instant >= row.Maximum || promotionDeadline <= instant)
        {
            return new(null, new PhotoReviewAppealDecisionResult.StateConflict());
        }
        var now = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
        var retention = now.AddHours(24) < row.Maximum ? now.AddHours(24) : row.Maximum;
        var decisionId = Guid.CreateVersion7(now);
        var caseVersion = checked(row.CaseVersion + 1);
        var photoVersion = checked(row.UploadVersion + 1);
        var appealVersion = checked(row.AppealVersion + 1);
        Guid? assetId = null;
        if (approve && finalVariants is not null && promotion is { } promoted)
        {
            if (!PhotoFormats.TryParseContentType(row.DetectedType, out var format))
            {
                return new(null, new PhotoReviewAppealDecisionResult.Unavailable());
            }
            var asset = MediaAsset.Approve(row.UploadId, row.OwnerId, format, row.Width, row.Height,
                Convert.FromHexString(row.OriginalSha256), now);
            db.Add(asset);
            foreach (var variant in finalVariants)
            {
                var kind = PhotoVariants.All.Single(v => PhotoVariants.Name(v) == variant.Kind);
                db.Add(MediaVariant.Create(asset.Id, kind, ObjectKey.ForPromotedDerivative(promoted, kind),
                    variant.Width, variant.Height, variant.SizeBytes, now));
            }
            await db.SaveChangesAsync(ct);
            if (await db.Database.ExecuteSqlAsync($"""
                update media.review_promotions set committed_at={now},asset_id={asset.Id}
                where generation={promoted} and committed_at is null and asset_id is null
                  and writer_settled_at is not null and prepare_deadline>{instant}
                  and cleanup_requested_at is null and cleanup_token is null and deleted_at is null
                  and appeal_id={appealId} and appeal_version={row.AppealVersion}
                """, ct) != 1)
            {
                throw new InvalidOperationException("The guarded appeal promotion must commit exactly once.");
            }
            assetId = asset.Id;
        }
        var decision = approve ? "approve" : "reject";
        var reason = approve ? "meets_policy" : "does_not_meet_policy";
        var status = approve ? "ClosedApproved" : "ClosedRejected";
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_decisions(id,case_id,case_version,actor_id,decision,reason_code,
              candidate_generation,policy_version,decided_at,candidate_retention_until_snapshot,history_retain_until,
              appeal_id,original_decision_id)
            values({decisionId},{row.CaseId},{caseVersion},{actor.UserId},{decision},{reason},{row.Generation},
              {row.PolicyVersion},{now},{retention},{now.AddYears(1)},{appealId},{row.OriginalDecisionId})
            """, ct);
        foreach (var variant in variants)
        {
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.photo_review_decision_variants(decision_id,variant,size_bytes,sha256)
                values({decisionId},{variant.Kind},{variant.SizeBytes},{variant.Sha256})
                """, ct);
        }
        var photoState = approve ? "Approved" : "Rejected";
        var rejection = approve ? null : "PHOTO_REVIEW_REJECTED";
        var changedUpload = await db.Database.ExecuteSqlAsync($"""
            update media.uploads set state={photoState},rejection_code={rejection},version={photoVersion}
            where id={row.UploadId} and version={row.UploadVersion} and state='Rejected' and rejection_code='PHOTO_REVIEW_REJECTED'
            """, ct);
        var changedCase = await db.Database.ExecuteSqlAsync($"update safety.photo_review_cases set version={caseVersion} where id={row.CaseId} and version={row.CaseVersion}", ct);
        var changedAppeal = await db.Database.ExecuteSqlAsync($"""
            update safety.photo_review_appeals set status={status},closed_at={now},version={appealVersion}
            where id={appealId} and version={row.AppealVersion} and status='Open' and closed_at is null
            """, ct);
        var changedCandidate = await db.Database.ExecuteSqlAsync($"""
            update media.review_candidates set retention_until={retention},cleanup_requested_at={now},
              cleanup_available_at=least(cleanup_available_at,{now})
            where generation={row.Generation} and cleanup_requested_at is null and cleanup_audit_id is null
              and cleanup_token is null and deleted_at is null and retention_until={row.Maximum}
            """, ct);
        if (changedUpload != 1 || changedCase != 1 || changedAppeal != 1 || changedCandidate != 1)
        {
            throw new InvalidOperationException("All guarded appeal decision projections must change exactly once.");
        }
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_appeal_decision_commands(actor_id,key_hash,intent_sha256,appeal_id,decision_id,
              case_id,result_appeal_version,result_case_version,result_photo_version,result_decision,result_asset_id,created_at)
            values({actor.UserId},{hash},{intent},{appealId},{decisionId},{row.CaseId},{appealVersion},
              {caseVersion},{photoVersion},{decision},{assetId},{now})
            """, ct);
        await new AuditTrail(db, time).AppendAsync(new AuditRecord(AuditActorKind.Moderator, actor.UserId,
            approve ? AuditAction.PhotoReviewAppealApproved : AuditAction.PhotoReviewAppealRejected,
            AuditTargetTypes.PhotoReviewDecision, decisionId, targetVersion: caseVersion), ct);
        if (!await AuthorizeAsync(access, actor, ct))
        {
            return new(null, new PhotoReviewAppealDecisionResult.Forbidden());
        }
        instant = time.GetUtcNow();
        if (instant >= row.Maximum || promotionDeadline <= instant)
        {
            return new(null, new PhotoReviewAppealDecisionResult.StateConflict());
        }
        var receipt = await ReplayAsync(db, actor.UserId, hash, intent, ct)
            ?? throw new InvalidOperationException("An appeal decision requires its persisted receipt.");
        await transaction.CommitAsync(ct);
        return new(null, receipt);
    }

    private static async Task<bool> AuthorizeAsync(PhotoReviewAccess access, PhotoReviewActor actor, CancellationToken ct) =>
        await access.AuthorizeAsync(actor, PhotoReviewCapability.Appeal, ct)
        && await access.AuthorizeAsync(actor, PhotoReviewCapability.Evidence, ct);

    private static bool ValidManifest(List<Variant> variants, AppealState row) => variants.Count == 3
        && variants.Select(v => v.Kind).SequenceEqual(PhotoVariants.All.Select(PhotoVariants.Name).Order())
        && row.Width > 0 && row.Height > 0 && (long)row.Width * row.Height <= 20_000_000
        && variants.All(v => v.SizeBytes is > 0 and <= 16_777_216 && v.Sha256.Length == 32
            && PhotoVariants.All.Any(k => PhotoVariants.Name(k) == v.Kind
                && PhotoVariants.Fit(row.Width, row.Height, PhotoVariants.MaxSide(k)) == (v.Width, v.Height)))
        && variants.Sum(v => v.SizeBytes) <= 25_165_824;

    private static bool Matches(Evidence expected, Evidence current) => expected.State == current.State
        && expected.Variants.Length == current.Variants.Length
        && expected.Variants.Zip(current.Variants).All(p => p.First.Kind == p.Second.Kind
            && p.First.SizeBytes == p.Second.SizeBytes && p.First.Width == p.Second.Width && p.First.Height == p.Second.Height
            && CryptographicOperations.FixedTimeEquals(p.First.Sha256, p.Second.Sha256));

    private static async Task<PhotoReviewAppealDecisionResult?> ReplayAsync(DuoraDbContext db, Guid actor,
        byte[] hash, byte[] intent, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<Receipt>($"""
            select appeal_id as "AppealId",case_id as "CaseId",decision_id as "DecisionId",intent_sha256 as "Intent",
              result_appeal_version as "AppealVersion",result_case_version as "CaseVersion",result_photo_version as "PhotoVersion",
              result_decision as "Decision",result_asset_id as "AssetId"
            from safety.photo_review_appeal_decision_commands where actor_id={actor} and key_hash={hash}
            """).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }
        return CryptographicOperations.FixedTimeEquals(row.Intent, intent)
            ? new PhotoReviewAppealDecisionResult.Decided(row.AppealId, row.CaseId, row.DecisionId, row.AppealVersion,
                row.CaseVersion, row.PhotoVersion, row.Decision, row.AssetId)
            : new PhotoReviewAppealDecisionResult.KeyReused();
    }

    private async Task RetainCleanupAsync(Guid generation)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await using var db = await contexts.CreateDbContextAsync(budget.Token);
            var now = time.GetUtcNow();
            await db.Database.ExecuteSqlAsync($"""
                update media.review_promotions set cleanup_requested_at=coalesce(cleanup_requested_at,{now}),
                  cleanup_available_at=least(cleanup_available_at,{now})
                where generation={generation} and committed_at is null and deleted_at is null
                """, budget.Token);
        }
        catch (Exception error) when (error is IOException or NpgsqlException or DbUpdateException or OperationCanceledException or InvalidOperationException)
        {
            // The confirmed preparation intent remains a durable obligation for the existing promotion sweeper.
        }
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

    private sealed record Phase(Evidence? Evidence, PhotoReviewAppealDecisionResult? Result);
    private sealed record Discovered(Guid OwnerId, Guid UploadId, Guid CaseId, Guid Generation);
    private sealed record PromotionState(DateTimeOffset Deadline);
    private sealed record Variant(string Kind, int Width, int Height, long SizeBytes, byte[] Sha256);
    private sealed record AppealState(Guid OwnerId, Guid UploadId, long UploadVersion, Guid CaseId, long CaseVersion,
        Guid Generation, string PolicyVersion, long AppealVersion, Guid OriginalDecisionId, DateTimeOffset Maximum,
        DateTimeOffset RequestedAt, string DetectedType, int Width, int Height, string OriginalSha256);
    private sealed record Receipt(Guid AppealId, Guid CaseId, Guid DecisionId, byte[] Intent, long AppealVersion,
        long CaseVersion, long PhotoVersion, string Decision, Guid? AssetId);
    private sealed record Evidence(AppealState State, Variant[] Variants)
    {
        public override string ToString() => "PhotoReviewAppealDecisionEvidence";
    }
}
