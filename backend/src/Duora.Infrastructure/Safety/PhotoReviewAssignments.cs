using System.Data;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

internal abstract record PhotoReviewAssignmentResult
{
    internal sealed record Assigned(Guid CaseId, long Version) : PhotoReviewAssignmentResult;
    internal sealed record Forbidden : PhotoReviewAssignmentResult;
    internal sealed record NotFound : PhotoReviewAssignmentResult;
    internal sealed record PreconditionFailed : PhotoReviewAssignmentResult;
    internal sealed record KeyReused : PhotoReviewAssignmentResult;
    internal sealed record ReviewerUnavailable : PhotoReviewAssignmentResult;
    internal sealed record StateConflict : PhotoReviewAssignmentResult;
}

internal readonly record struct PhotoReviewCursor(DateTimeOffset AwaitingReviewAt, Guid CaseId);
internal sealed record PhotoReviewSummary(Guid CaseId, string State, long Version, DateTimeOffset AwaitingReviewAt, DateTimeOffset ReviewDeadline, string PolicyVersion);
internal sealed record PhotoReviewPage(IReadOnlyList<PhotoReviewSummary> Cases, PhotoReviewCursor? NextCursor);
internal sealed record PhotoReviewDetails(PhotoReviewSummary Case, string PolicyVersion);

internal abstract record PhotoReviewListResult
{
    internal sealed record Listed(PhotoReviewPage Page) : PhotoReviewListResult;
    internal sealed record Forbidden : PhotoReviewListResult;
}

internal abstract record PhotoReviewDetailsResult
{
    internal sealed record Found(PhotoReviewDetails Details) : PhotoReviewDetailsResult;
    internal sealed record Forbidden : PhotoReviewDetailsResult;
    internal sealed record NotFound : PhotoReviewDetailsResult;
}

/// <summary>
/// Explicit assignment and minimal metadata only. Not registered; image access and decisions are separate.
/// Each effect owns its READ COMMITTED transaction. Never continue a transaction after a lock error.
/// </summary>
internal sealed class PhotoReviewAssignments(DuoraDbContext db, TimeProvider time)
{
    internal async Task<PhotoReviewAssignmentResult> AssignAsync(
        PhotoReviewActor actor, Guid caseId, Guid reviewerId, long expectedVersion, IdempotencyKey key,
        CancellationToken cancellationToken)
    {
        RequireOwnTransaction();
        if (!IdempotencyKey.TryParse(key.Value, out _))
        {
            throw new ArgumentException("A validated idempotency key is required.", nameof(key));
        }
        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value));
        var intent = SHA256.HashData(Encoding.UTF8.GetBytes($"assign\n{caseId:D}\n{reviewerId:D}"));
        var access = new PhotoReviewAccess(db, time);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // No actor guard may survive into discovery of another, potentially lower, user's guard.
            await using (var preauthorization = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Assign, cancellationToken))
                {
                    return new PhotoReviewAssignmentResult.Forbidden();
                }
                if (await ReplayAsync(actor.UserId, keyHash, intent, cancellationToken) is { } confirmed)
                {
                    return confirmed;
                }
            }

            var discovered = await DiscoverAsync(caseId, cancellationToken);
            if (discovered is null)
            {
                return new PhotoReviewAssignmentResult.NotFound();
            }
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var usersPresent = await GuardUsersAsync([actor.UserId, discovered.OwnerId, reviewerId], cancellationToken);
            if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Assign, cancellationToken))
            {
                return new PhotoReviewAssignmentResult.Forbidden();
            }
            // A concurrent command may have committed since preauthorization. Replay precedes all mutable conditions.
            if (await ReplayAsync(actor.UserId, keyHash, intent, cancellationToken) is { } replay)
            {
                return replay;
            }
            if (!usersPresent)
            {
                return new PhotoReviewAssignmentResult.ReviewerUnavailable();
            }
            await LockCaseAsync(caseId, discovered, forAssignment: true, cancellationToken);
            if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Assign, cancellationToken))
            {
                return new PhotoReviewAssignmentResult.Forbidden();
            }
            var row = await ReadCaseAsync(caseId, discovered, cancellationToken);
            if (row is null)
            {
                continue; // Changed discovery: release EVERY lock before discovering and ordering again.
            }
            var now = time.GetUtcNow();
            if (!await IsLiveAsync(caseId, discovered, now, cancellationToken))
            {
                return new PhotoReviewAssignmentResult.StateConflict();
            }
            if (!await ReviewerReadyAsync(reviewerId, cancellationToken))
            {
                return new PhotoReviewAssignmentResult.ReviewerUnavailable();
            }
            if (row.Version != expectedVersion)
            {
                return new PhotoReviewAssignmentResult.PreconditionFailed();
            }
            var version = checked(row.Version + 1);
            var assignmentId = Guid.CreateVersion7(now);
            var changed = await db.Database.ExecuteSqlAsync($"""
                update safety.photo_review_cases set version={version} where id={caseId} and version={row.Version}
                """, cancellationToken);
            if (changed != 1)
            {
                throw new InvalidOperationException("The guarded case version must advance exactly once.");
            }
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.photo_review_assignments(id,case_id,reviewer_id,assigned_by,assigned_at,case_version)
                values({assignmentId},{caseId},{reviewerId},{actor.UserId},{now},{version})
                """, cancellationToken);
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.photo_review_commands(actor_id,operation,key_hash,case_id,intent_sha256,result_version,created_at)
                values({actor.UserId},'assign',{keyHash},{caseId},{intent},{version},{now})
                """, cancellationToken);
            await new AuditTrail(db, time).AppendAsync(new AuditRecord(AuditActorKind.Moderator, actor.UserId,
                AuditAction.PhotoReviewAssigned, AuditTargetTypes.PhotoReviewCase, caseId, targetVersion: version), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PhotoReviewAssignmentResult.Assigned(caseId, version);
        }
        return new PhotoReviewAssignmentResult.StateConflict();
    }

    internal async Task<PhotoReviewListResult> ListAssignedAsync(
        PhotoReviewActor actor, PhotoReviewCursor? cursor, int limit, CancellationToken cancellationToken)
    {
        RequireOwnTransaction();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await new PhotoReviewAccess(db, time).AuthorizeAsync(actor, PhotoReviewCapability.Evidence, cancellationToken))
        {
            return new PhotoReviewListResult.Forbidden();
        }
        var now = time.GetUtcNow();
        var cursorAt = cursor?.AwaitingReviewAt ?? DateTimeOffset.MinValue;
        var cursorId = cursor?.CaseId ?? Guid.Empty;
        var hasCursor = cursor.HasValue;
        var rows = await db.Database.SqlQuery<PhotoReviewSummary>($"""
            select r.id as "CaseId",u.state as "State",r.version as "Version",
              r.awaiting_review_at as "AwaitingReviewAt",r.review_deadline as "ReviewDeadline",r.policy_version as "PolicyVersion"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id
            join identity.users owner on owner.id=u.owner_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.upload_id=u.id and c.case_id=r.id
            where u.state='AwaitingReview' and u.version=c.upload_version+1
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active')
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.deleted_at is null and c.retention_until>{now}
              and r.review_deadline>{now}
              and (select a.reviewer_id from safety.photo_review_assignments a where a.case_id=r.id
                order by a.case_version desc limit 1)={actor.UserId}
              and (not {hasCursor} or (r.awaiting_review_at,r.id)>({cursorAt},{cursorId}))
            order by r.awaiting_review_at,r.id limit {limit + 1}
            """).ToListAsync(cancellationToken);
        var items = rows.Take(limit).ToArray();
        PhotoReviewCursor? next = rows.Count > limit ? new(items[^1].AwaitingReviewAt, items[^1].CaseId) : null;
        return new PhotoReviewListResult.Listed(new PhotoReviewPage(items, next));
    }

    internal async Task<PhotoReviewDetailsResult> FindAssignedAsync(
        PhotoReviewActor actor, Guid caseId, CancellationToken cancellationToken)
    {
        RequireOwnTransaction();
        var access = new PhotoReviewAccess(db, time);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using (var preauthorization = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Evidence, cancellationToken))
                {
                    return new PhotoReviewDetailsResult.Forbidden();
                }
            }
            var discovered = await DiscoverAsync(caseId, cancellationToken);
            if (discovered is null)
            {
                return new PhotoReviewDetailsResult.NotFound();
            }
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var usersPresent = await GuardUsersAsync([actor.UserId, discovered.OwnerId], cancellationToken);
            if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Evidence, cancellationToken))
            {
                return new PhotoReviewDetailsResult.Forbidden();
            }
            if (!usersPresent)
            {
                return new PhotoReviewDetailsResult.NotFound();
            }
            await LockCaseAsync(caseId, discovered, forAssignment: false, cancellationToken);
            if (!await access.AuthorizeAsync(actor, PhotoReviewCapability.Evidence, cancellationToken))
            {
                return new PhotoReviewDetailsResult.Forbidden();
            }
            var row = await ReadCaseAsync(caseId, discovered, cancellationToken);
            if (row is null)
            {
                continue;
            }
            if (!await IsLiveAsync(caseId, discovered, time.GetUtcNow(), cancellationToken)
                || !await IsAssignedAsync(caseId, actor.UserId, cancellationToken))
            {
                return new PhotoReviewDetailsResult.NotFound();
            }
            return new PhotoReviewDetailsResult.Found(new PhotoReviewDetails(new PhotoReviewSummary(
                caseId, "AwaitingReview", row.Version, row.AwaitingReviewAt, row.ReviewDeadline, row.PolicyVersion), row.PolicyVersion));
        }
        return new PhotoReviewDetailsResult.NotFound();
    }

    private void RequireOwnTransaction()
    {
        if (db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Photo review assignments own their complete READ COMMITTED transaction.");
        }
    }

    private async Task<PhotoReviewAssignmentResult?> ReplayAsync(
        Guid actorId, byte[] keyHash, byte[] intent, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<Receipt>($"""
            select case_id as "CaseId",intent_sha256 as "IntentSha256",result_version as "ResultVersion"
            from safety.photo_review_commands where actor_id={actorId} and operation='assign' and key_hash={keyHash}
            """).ToListAsync(cancellationToken);
        return rows.SingleOrDefault() is not { } receipt ? null
            : CryptographicOperations.FixedTimeEquals(receipt.IntentSha256, intent)
                ? new PhotoReviewAssignmentResult.Assigned(receipt.CaseId, receipt.ResultVersion)
                : new PhotoReviewAssignmentResult.KeyReused();
    }

    private async Task<DiscoveredCase?> DiscoverAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<DiscoveredCase>($"""
            select u.owner_id as "OwnerId",u.id as "UploadId",r.candidate_generation as "Generation"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id where r.id={caseId}
            """).ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    private async Task<bool> GuardUsersAsync(Guid[] users, CancellationToken cancellationToken)
    {
        var ordered = users.Distinct().Order().ToArray();
        var allPresent = true;
        await db.Database.ExecuteSqlAsync($"set local lock_timeout='2s'", cancellationToken);
        foreach (var user in ordered)
        {
            await db.Database.ExecuteSqlAsync($"""
                insert into safety.user_guards(user_id) select id from identity.users where id={user} on conflict do nothing
                """, cancellationToken);
            var guards = await db.Database.SqlQuery<Guid>($"""
                select user_id as "Value" from safety.user_guards where user_id={user} for update
                """).ToListAsync(cancellationToken);
            allPresent &= guards.Count == 1;
        }
        foreach (var user in ordered)
        {
            await db.Database.ExecuteSqlAsync($"select 1 from identity.users where id={user} for share", cancellationToken);
            await db.Database.ExecuteSqlAsync($"""
                select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
                where ur.user_id={user} order by ur.role_id for share of ur,r nowait
                """, cancellationToken);
        }
        return allPresent;
    }

    private async Task LockCaseAsync(Guid caseId, DiscoveredCase discovered, bool forAssignment, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={discovered.UploadId} for share", cancellationToken);
        if (forAssignment)
        {
            await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={caseId} for update", cancellationToken);
        }
        else
        {
            await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={caseId} for share", cancellationToken);
        }
        await db.Database.ExecuteSqlAsync($"""
            select 1 from media.review_candidates where generation={discovered.Generation} for share
            """, cancellationToken);
    }

    private async Task<CaseState?> ReadCaseAsync(Guid caseId, DiscoveredCase discovered, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<CaseState>($"""
            select r.version as "Version",r.awaiting_review_at as "AwaitingReviewAt",r.review_deadline as "ReviewDeadline",
              r.policy_version as "PolicyVersion"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id
            where r.id={caseId} and u.id={discovered.UploadId} and u.owner_id={discovered.OwnerId}
              and r.candidate_generation={discovered.Generation}
            """).ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    private Task<bool> IsLiveAsync(Guid caseId, DiscoveredCase discovered, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"""
            select exists(select 1 from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id
              join identity.users owner on owner.id=u.owner_id
              join media.review_candidates c on c.generation=r.candidate_generation and c.upload_id=u.id and c.case_id=r.id
              where r.id={caseId} and u.id={discovered.UploadId} and u.owner_id={discovered.OwnerId}
                and r.candidate_generation={discovered.Generation} and r.review_deadline>{now}
                and u.state='AwaitingReview' and u.version=c.upload_version+1
                and owner.email_confirmed and owner.status in ('PendingEligibility','Active')
                and c.committed_at is not null and c.writer_settled_at is not null
                and c.cleanup_requested_at is null and c.deleted_at is null and c.retention_until>{now}) as "Value"
            """).SingleAsync(cancellationToken);

    private Task<bool> ReviewerReadyAsync(Guid user, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"""
            select exists(select 1 from identity.users u where u.id={user}
              and u.status='Active' and u.email_confirmed and u.two_factor_enabled and u.temporary_password_expires_at is null
              and exists(select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
                where ur.user_id=u.id and r.name='moderator')
              and coalesce((select enabled from safety.photo_review_grants where user_id=u.id and capability='evidence'
                order by version desc limit 1),false)
              and coalesce((select enabled from safety.photo_review_grants where user_id=u.id and capability='decide'
                order by version desc limit 1),false)) as "Value"
            """).SingleAsync(cancellationToken);

    private Task<bool> IsAssignedAsync(Guid caseId, Guid user, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"""
            select coalesce((select reviewer_id={user} from safety.photo_review_assignments
              where case_id={caseId} order by case_version desc limit 1),false) as "Value"
            """).SingleAsync(cancellationToken);

    private sealed record Receipt(Guid CaseId, byte[] IntentSha256, long ResultVersion);
    private sealed record DiscoveredCase(Guid OwnerId, Guid UploadId, Guid Generation);
    private sealed record CaseState(long Version, DateTimeOffset AwaitingReviewAt, DateTimeOffset ReviewDeadline, string PolicyVersion);
}
