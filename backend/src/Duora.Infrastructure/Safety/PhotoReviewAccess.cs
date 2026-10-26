using System.Data;
using Duora.Core.Identity;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Duora.Infrastructure.Safety;

internal enum PhotoReviewCapability
{
    Assign,
    Evidence,
    Decide,
    Appeal,
    Preserve,
}

/// <summary>Identifiers from the authenticated boundary, never a public authentication credential.</summary>
internal readonly record struct PhotoReviewActor(Guid UserId, Guid SessionId);

/// <summary>
/// Explicit permissions and live transactional authorization (ADR-0035). Not registered or exposed yet.
/// This is not case authorization: a caller must also enforce assignment, candidate and owner guards.
/// A lock conflict aborts the entire transaction; never continue an effect after catching it.
/// </summary>
internal sealed class PhotoReviewAccess(DuoraDbContext db, TimeProvider time)
{
    /// <summary>Preparation for the audited terminal command. No route, CLI or real grant is enabled here.</summary>
    internal async Task<bool> SetCapabilityAsync(
        Guid userId, PhotoReviewCapability capability, bool enabled, OperatorName operatorName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operatorName);
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        var name = CapabilityName(capability);
        if (db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Permission changes own their complete READ COMMITTED transaction.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await GuardUserAsync(userId, cancellationToken)
            || (enabled && !await IsReadyModeratorAsync(userId, cancellationToken)))
        {
            return false;
        }

        var history = await db.Database.SqlQuery<GrantState>($"""
            select enabled as "Enabled",version as "Version" from safety.photo_review_grants
            where user_id={userId} and capability={name} order by version desc limit 1
            """).ToListAsync(cancellationToken);
        var current = history.SingleOrDefault();
        if ((current?.Enabled ?? false) == enabled)
        {
            return false;
        }

        var version = checked((current?.Version ?? 0) + 1);
        var now = time.GetUtcNow();
        var grantId = Guid.CreateVersion7(now);
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_grants(id,user_id,capability,version,enabled,occurred_at)
            values({grantId},{userId},{name},{version},{enabled},{now})
            """, cancellationToken);
        await new AuditTrail(db, time).AppendAsync(AuditRecord.ByTerminalOperator(
            operatorName,
            enabled ? AuditAction.PhotoReviewPermissionGranted : AuditAction.PhotoReviewPermissionRevoked,
            AuditTargetTypes.PhotoReviewGrant, grantId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Caller keeps the transaction through its audited effect. Acquire any additional users' guards in UUID
    /// order before calling this method; a separate completed pre-authorization is not a cached permission.
    /// </summary>
    internal async Task<bool> AuthorizeAsync(
        PhotoReviewActor actor, PhotoReviewCapability capability, CancellationToken cancellationToken)
    {
        RequireReadCommitted();
        var name = CapabilityName(capability);
        if (actor.UserId == Guid.Empty || actor.SessionId == Guid.Empty
            || !await GuardUserAsync(actor.UserId, cancellationToken))
        {
            return false;
        }

        await db.Database.ExecuteSqlAsync($"""
            select 1 from identity.login_sessions where id={actor.SessionId} and user_id={actor.UserId} for share
            """, cancellationToken);
        // Capture time after all possible lock waits, not when a request entered the queue.
        var now = time.GetUtcNow();
        var proofs = await db.Database.SqlQuery<SessionProof>($"""
            select s.totp_verified_at as "TotpVerifiedAt"
            from identity.users u join identity.login_sessions s on s.user_id=u.id
            where u.id={actor.UserId} and s.id={actor.SessionId}
              and u.status='Active' and u.email_confirmed and u.two_factor_enabled
              and u.temporary_password_expires_at is null
              and exists(select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
                where ur.user_id=u.id and r.name='moderator')
              and s.policy='Administrative' and s.security_stamp=u.security_stamp
              and s.revoked_at is null and s.idle_expires_at>{now} and s.absolute_expires_at>{now}
              and s.mfa_verified_at is not null and s.mfa_verified_at<={now}
              and coalesce((select g.enabled from safety.photo_review_grants g
                where g.user_id=u.id and g.capability={name} order by g.version desc limit 1),false)
            """).ToListAsync(cancellationToken);
        return proofs is [var proof]
            && (!RequiresRecentTotp(capability) || RecentAuthentication.IsRecent(proof.TotpVerifiedAt, now));
    }

    private void RequireReadCommitted()
    {
        if (db.Database.CurrentTransaction is not { } transaction
            || transaction.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted
            || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Live review authorization requires an explicit READ COMMITTED transaction.");
        }
    }

    private async Task<bool> GuardUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"set local lock_timeout='2s'", cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            insert into safety.user_guards(user_id) select id from identity.users where id={userId}
            on conflict do nothing
            """, cancellationToken);
        var guards = await db.Database.SqlQuery<Guid>($"""
            select user_id as "Value" from safety.user_guards where user_id={userId} for update
            """).ToListAsync(cancellationToken);
        if (guards.Count != 1)
        {
            return false;
        }

        await db.Database.ExecuteSqlAsync($"select 1 from identity.users where id={userId} for share", cancellationToken);
        // D13 UPDATE locks user_roles before users. Never wait for those rows while holding users:
        // NOWAIT aborts this whole transaction instead of forming a users -> roles -> users cycle.
        await db.Database.ExecuteSqlAsync($"""
            select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
            where ur.user_id={userId} order by ur.role_id for share of ur,r nowait
            """, cancellationToken);
        return true;
    }

    private Task<bool> IsReadyModeratorAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"""
            select exists(select 1 from identity.users u where u.id={userId}
              and u.status='Active' and u.email_confirmed and u.two_factor_enabled
              and u.temporary_password_expires_at is null
              and exists(select 1 from identity.user_roles ur join identity.roles r on r.id=ur.role_id
                where ur.user_id=u.id and r.name='moderator')) as "Value"
            """).SingleAsync(cancellationToken);

    private static bool RequiresRecentTotp(PhotoReviewCapability capability) =>
        capability is PhotoReviewCapability.Decide or PhotoReviewCapability.Appeal or PhotoReviewCapability.Preserve;

    private static string CapabilityName(PhotoReviewCapability capability) => capability switch
    {
        PhotoReviewCapability.Assign => "assign",
        PhotoReviewCapability.Evidence => "evidence",
        PhotoReviewCapability.Decide => "decide",
        PhotoReviewCapability.Appeal => "appeal",
        PhotoReviewCapability.Preserve => "preserve",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown review permission."),
    };

    private sealed record GrantState(bool Enabled, long Version);
    private sealed record SessionProof(DateTimeOffset? TotpVerifiedAt);
}
