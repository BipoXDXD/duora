using System.Diagnostics;
using System.Text.Json;
using Duora.Infrastructure.Audit;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Participation;

/// <summary>Events-owned writes on the caller's transaction, after all user/account and event locks.</summary>
internal static class RegistrationTransitions
{
    public static async Task WithdrawForSuspensionAsync(NpgsqlConnection connection, RegistrationLockPlan plan,
        Guid subject, Guid suspensionId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var eventId in plan.Events)
        {
            string? previous;
            await using (var query = Command(connection, """
                select r.state from events.registrations r join events.events e on e.id=r.event_id
                where r.event_id=$1 and r.user_id=$2 and r.state in ('Waitlisted','Confirmed','CheckedIn')
                  and e.state not in ('Completed','Cancelled')
                """, eventId, subject))
            {
                previous = (string?)await query.ExecuteScalarAsync(ct);
            }
            if (previous is null)
            {
                continue;
            }
            var changed = await Execute(connection, """
                update events.registrations set withdrawn_from_state=state, withdrawn_waitlist_position=waitlist_position,
                  state='Withdrawn', waitlist_position=null, withdrawn_at=$3, withdrawn_by_suspension_id=$4, version=version+1
                where event_id=$1 and user_id=$2 and state in ('Waitlisted','Confirmed','CheckedIn')
                """, ct, eventId, subject, now, suspensionId);
            if (changed != 1)
            {
                throw new InvalidOperationException("Registration changed under its event lock.");
            }
            await Execute(connection, """
                update events.events set confirmed_count=confirmed_count-$2,version=version+1 where id=$1
                """, ct, eventId, previous is "Confirmed" or "CheckedIn" ? 1 : 0);
            await AuditAsync(connection, AuditAction.RegistrationWithdrawnBySuspension, eventId, now, ct);
            await PromoteAsync(connection, eventId, now, ct);
        }
    }

    public static async Task PromoteAsync(NpgsqlConnection connection, Guid eventId, DateTimeOffset now,
        CancellationToken ct)
    {
        // All waitlisted users/accounts have already been guarded. Re-read actual state and eligibility, ordered by
        // the never-reused queue position; temporarily ineligible people keep their position and history.
        List<Guid> promoted = [];
        await using (var query = Command(connection, """
            select r.user_id from events.registrations r join identity.users u on u.id=r.user_id
              join events.events e on e.id=r.event_id
            where r.event_id=$1 and r.state='Waitlisted' and e.state='Published' and $2<e.starts_at
              and u.status='Active' and u.email_confirmed
              and exists(select 1 from identity.selfie_verifications v where v.user_id=u.id and v.state='Passed')
            order by r.waitlist_position
            limit (select capacity-confirmed_count from events.events where id=$1)
            """, eventId, now))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                promoted.Add(reader.GetGuid(0));
            }
        }
        foreach (var user in promoted)
        {
            var changed = await Execute(connection, """
                update events.registrations set state='Confirmed',waitlist_position=null,confirmed_at=$3,version=version+1
                where event_id=$1 and user_id=$2 and state='Waitlisted'
                """, ct, eventId, user, now);
            if (changed != 1)
            {
                throw new InvalidOperationException("Waitlist changed under its event lock.");
            }
            await Execute(connection,
                "update events.events set confirmed_count=confirmed_count+1,version=version+1 where id=$1", ct, eventId);
            await AuditAsync(connection, AuditAction.RegistrationPromoted, eventId, now, ct);
            // Durable fact for notification consumers; no external effect while holding business locks.
            var payload = JsonSerializer.Serialize(new { eventId, userId = user });
            await Execute(connection, """
                insert into ops.outbox_events(id,type,schema_version,resource_type,resource_id,resource_version,payload,occurred_at)
                select $1,'events.waitlisted_registration_promoted',1,'event',id,version,$3::jsonb,$4
                  from events.events where id=$2
                """, ct, Guid.CreateVersion7(now), eventId, payload, now);
        }
    }

    private static Task<int> AuditAsync(NpgsqlConnection connection, AuditAction action, Guid eventId,
        DateTimeOffset now, CancellationToken ct) => Execute(connection, """
            insert into ops.audit_events(occurred_at,actor_kind,action,target_type,target_id,target_version,correlation_id)
            select $1,'System',$2,'event',id,version,nullif($4,'') from events.events where id=$3
            """, ct, now, action.Code, eventId, Activity.Current?.TraceId.ToHexString() ?? "");
}
