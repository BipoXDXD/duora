using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

/// <summary>
/// Live authorization of a participant acting on their own photos, inside the caller's READ COMMITTED transaction.
/// Lock order (modules.md): Safety guard of the owner, then Identity user, roles and session. Only a pure participant
/// with a live normal session qualifies, so a privileged account (moderator included) never owns reviewed photos.
/// </summary>
internal static class PhotoOwnerAccess
{
    internal static async Task<bool> AuthorizeAsync(
        DuoraDbContext db, TimeProvider time, PhotoReviewActor owner, CancellationToken cancellationToken)
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
        var now = time.GetUtcNow();
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
}
