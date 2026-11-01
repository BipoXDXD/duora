using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Participation;

/// <summary>
/// Preliminary resources, re-read after guards and coordinators. Discovering another user/event requires a full
/// transaction restart, never a late guard (spec 10.4). Removed candidates need no additional locks.
/// </summary>
internal sealed record RegistrationLockPlan(Guid[] Events, Guid[] Users)
{
    public static async Task<RegistrationLockPlan> ForSuspensionAsync(NpgsqlConnection connection, Guid subject,
        Guid? moderator, CancellationToken ct)
    {
        var events = await IdsAsync(connection, """
            select event_id from events.registrations r join events.events e on e.id=r.event_id
              where r.user_id=$1 and r.state in ('Waitlisted','Confirmed','CheckedIn')
                and e.state not in ('Completed','Cancelled')
            union select event_id from events.pairings
              where (user_low=$1 or user_high=$1) and released_at is null
            order by event_id
            """, ct, subject);
        return await ForEventsAsync(connection, events,
            moderator is { } actor ? [subject, actor] : [subject], ct);
    }

    public static Task<RegistrationLockPlan> ForRegistrationAsync(NpgsqlConnection connection, Guid eventId,
        Guid user, CancellationToken ct) => ForEventsAsync(connection, [eventId], [user], ct);

    private static async Task<RegistrationLockPlan> ForEventsAsync(NpgsqlConnection connection, Guid[] events,
        Guid[] users, CancellationToken ct)
    {
        var waiting = await IdsAsync(connection,
            "select distinct user_id from events.registrations where event_id=any($1) and state='Waitlisted' order by user_id",
            ct, events);
        return new(events, [.. users.Concat(waiting).Distinct().Order()]);
    }

    public async Task LockEventsAsync(NpgsqlConnection connection, CancellationToken ct) =>
        await Execute(connection, "select 1 from events.events where id=any($1) order by id for update", ct, Events);

    public void RequireCovered(RegistrationLockPlan current)
    {
        if (current.Events.Except(Events).Any() || current.Users.Except(Users).Any())
        {
            throw new RegistrationPlanChangedException();
        }
    }

    private static async Task<Guid[]> IdsAsync(NpgsqlConnection connection, string sql, CancellationToken ct,
        params object[] values)
    {
        await using var query = Command(connection, sql, values);
        await using var reader = await query.ExecuteReaderAsync(ct);
        List<Guid> ids = [];
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }
        return [.. ids];
    }
}

internal sealed class RegistrationPlanChangedException : Exception;
