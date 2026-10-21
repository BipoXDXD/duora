using System.Diagnostics.Metrics;
using Duora.Core.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Participation;

public sealed record ParticipationEvent(Guid Id, string Title, DateTimeOffset StartsAt, string State, string? TimeZone);
public sealed record ParticipationStatus(string State, Guid? RoomId);
public enum ParticipationOutcome { Done, Unavailable, Conflict, Invalid }

public sealed class ParticipationOptions
{
    public bool MatchingEnabled { get; set; } = true;

    /// <summary>Optional cutover for subsequently published rounds. Null preserves V2; persisted rounds never change version.</summary>
    public DateTimeOffset? RegionalMatchingStartsAt { get; set; }
}

public static class ParticipationServices
{
    public static IServiceCollection AddParticipation(this IServiceCollection services)
    {
        services.AddOptions<ParticipationOptions>().BindConfiguration("Participation");
        return services.AddSingleton<ParticipationService>();
    }
}

/// <summary>Coordinates ordered user guards, event/round and session locks in one PostgreSQL transaction.</summary>
public sealed partial class ParticipationService(NpgsqlDataSource source, TimeProvider time, IOptions<ParticipationOptions>? options = null)
{
    private static readonly Meter MatchingMeter = new("Duora.Matching");
    private static readonly Counter<long> RoundsBuilt = MatchingMeter.CreateCounter<long>("duora.matching.rounds");
    private static readonly Counter<long> PairsBuilt = MatchingMeter.CreateCounter<long>("duora.matching.pairs");
    private static readonly Counter<long> CandidatesWaiting = MatchingMeter.CreateCounter<long>("duora.matching.waiting_candidates");
    private static readonly HashSet<string> EventTimeZones = TimeZoneInfo.GetSystemTimeZones()
        .Where(zone => zone.HasIanaId).Select(zone => zone.Id).Append("UTC").ToHashSet(StringComparer.Ordinal);
    private bool MatchingEnabled => options?.Value.MatchingEnabled ?? true;

    private DateTimeOffset Now => PreciseClock.Truncate(time.GetUtcNow());
    private sealed class PreciseClock(TimeProvider inner) : TimeProvider
    {
        public static DateTimeOffset Truncate(DateTimeOffset instant) => new(instant.Ticks - instant.Ticks % 10, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Truncate(inner.GetUtcNow());
    }

    internal Func<string, CancellationToken, Task>? ObserveMatchingStage { get; set; }

    public async Task<IReadOnlyList<ParticipationEvent>> EventsAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = Command(connection, "select id,title,starts_at,state,time_zone from events.events where state in ('Published','Running') order by starts_at,id limit 100");
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<ParticipationEvent> result = [];
        while (await reader.ReadAsync(ct))
        {
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    public async Task<Guid?> PublishAsync(string title, DateTimeOffset startsAt, int capacity, Guid contentId, Guid administrator, CancellationToken ct, string? timeZone = null)
    {
        if ((timeZone is not null && !EventTimeZones.Contains(timeZone)) || string.IsNullOrWhiteSpace(title) || title.Length > 120 || capacity is < 1 or > 100 || startsAt <= Now.AddMinutes(10))
        {
            return null;
        }

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Guard(connection, [administrator], ct);
        await using (var administratorQuery = Command(connection, """
            select exists(select 1 from identity.users u join identity.user_roles ur on ur.user_id=u.id
              join identity.roles r on r.id=ur.role_id where u.id=$1 and u.status='Active' and r.name='product_admin')
            """, administrator))
        {
            if (!(bool)(await administratorQuery.ExecuteScalarAsync(ct))!)
            {
                return null;
            }
        }
        await using (var publication = Command(connection, "select exists(select 1 from experiences.experience_versions where id=$1 and editorial_state='Published')", contentId))
        {
            if (!(bool)(await publication.ExecuteScalarAsync(ct))!)
            {
                return null;
            }
        }
        // Loading validates the complete bilingual content; an unpublished or malformed catalog cannot open an event.
        var kind = await ContentKindAsync(connection, contentId, ct);
        if (kind == "unlikely-routes" ? await RoutesContentAsync(connection, contentId, ct) is null : kind != "constellation" || await ContentAsync(connection, contentId, ct) is null)
        {
            return null;
        }

        var id = Guid.CreateVersion7();
        await Execute(connection, "insert into events.events(id,title,state,starts_at,capacity,experience_version_id,time_zone) values($1,$2,'Published',$3,$4,$5,$6)", ct, id, title, startsAt, capacity, contentId, (object?)timeZone ?? DBNull.Value);
        for (var number = 1; number <= 3; number++)
        {
            var round = Guid.CreateVersion7();
            var start = startsAt.AddMinutes((number - 1) * 12);
            var algorithm = options?.Value.RegionalMatchingStartsAt is { } cutover && start >= cutover
                ? PairingAlgorithmVersion.GreedyByPriorityV3 : PairingAlgorithmVersion.GreedyByPriorityV2;
            await Execute(connection, "insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed,algorithm_version) values($1,$2,$3,'Scheduled',$4,$5,$6,$7)", ct, round, id, number, start, start.AddMinutes(12), Random.Shared.NextInt64(), (int)algorithm);
            await ScheduleAsync(connection, "build-round", round, start, ct);
            await ScheduleAsync(connection, "close-round", round, start.AddMinutes(12), ct);
        }
        await transaction.CommitAsync(ct);
        return id;
    }

    public async Task<ParticipationOutcome> RegisterAsync(Guid eventId, Guid user, bool checkIn, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await Guard(connection, [user], ct);
        await Execute(connection, "select 1 from events.events where id=$1 for update", ct, eventId);
        if (!await Eligible(connection, user, ct))
        {
            return ParticipationOutcome.Unavailable;
        }

        await using var query = Command(connection, "select starts_at,capacity,confirmed_count,next_waitlist_position,state from events.events where id=$1", eventId);
        DateTimeOffset starts;
        int capacity;
        int count;
        long position;
        string state;
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return ParticipationOutcome.Unavailable;
            }

            starts = reader.GetFieldValue<DateTimeOffset>(0);
            capacity = reader.GetInt32(1);
            count = reader.GetInt32(2);
            position = reader.GetInt64(3);
            state = reader.GetString(4);
        }
        var now = Now;
        if (state != "Published" || now >= starts)
        {
            return ParticipationOutcome.Unavailable;
        }

        if (checkIn)
        {
            if (now < starts.AddMinutes(-10))
            {
                return ParticipationOutcome.Unavailable;
            }

            var changed = await Execute(connection, "update events.registrations set state='CheckedIn',checked_in_at=coalesce(checked_in_at,$3) where event_id=$1 and user_id=$2 and state in ('Confirmed','CheckedIn')", ct, eventId, user, now);
            if (changed == 0)
            {
                return ParticipationOutcome.Unavailable;
            }
        }
        else
        {
            var inserted = await Execute(connection, """
                insert into events.registrations(event_id,user_id,state,waitlist_position,registered_at)
                values($1,$2,$3,$4,$5) on conflict(event_id,user_id) do nothing
                """, ct, eventId, user, count < capacity ? "Confirmed" : "Waitlisted", count < capacity ? DBNull.Value : position, now);
            if (inserted == 1)
            {
                await Execute(connection, count < capacity
                    ? "update events.events set confirmed_count=confirmed_count+1,version=version+1 where id=$1"
                    : "update events.events set next_waitlist_position=next_waitlist_position+1,version=version+1 where id=$1", ct, eventId);
            }
        }
        await tx.CommitAsync(ct);
        return ParticipationOutcome.Done;
    }

    public async Task<ParticipationStatus?> StatusAsync(Guid eventId, Guid user, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        if (!await Eligible(connection, user, ct))
        {
            return null;
        }

        await using var command = Command(connection, """
            select r.state,s.id,s.state from events.registrations r
            left join lateral(select gs.id,gs.state from events.pairings p join experiences.game_sessions gs on gs.pairing_id=p.id
              where p.event_id=r.event_id and (p.user_low=r.user_id or p.user_high=r.user_id)
              and not exists(select 1 from safety.blocks b where (b.blocker_id=p.user_low and b.blocked_id=p.user_high) or (b.blocker_id=p.user_high and b.blocked_id=p.user_low))
              order by p.created_at desc limit 1) s on true
            where r.event_id=$1 and r.user_id=$2
            """, eventId, user);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new("Unregistered", null);
        }

        return reader.IsDBNull(1) ? new(reader.GetString(0) == "CheckedIn" ? "Waiting" : reader.GetString(0), null)
            : new(reader.GetString(2) is "Aborted" or "Expired" ? "Unavailable" : reader.GetString(2), reader.GetGuid(1));
    }

    public async Task BuildRoundPairingsAsync(Guid roundId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await BuildRoundAsync(connection, roundId, ct);
        await transaction.CommitAsync(ct);
    }

    private async Task BuildRoundAsync(NpgsqlConnection connection, Guid roundId, CancellationToken ct)
    {
        if (!MatchingEnabled)
        {
            return;
        }
        // Preliminary IDs are suggestions only. The same set is revalidated after acquiring its user guards.
        List<Guid> ids = [];
        await using (var query = Command(connection, "select rg.user_id from events.registrations rg join events.rounds r on r.event_id=rg.event_id where r.id=$1 and rg.state='CheckedIn' order by rg.user_id", roundId))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        if (ObserveMatchingStage is { } observed)
        {
            await observed("CandidatesRead", ct);
        }
        await Guard(connection, ids, ct);
        if (ObserveMatchingStage is { } guarded)
        {
            await guarded("CandidatesGuarded", ct);
        }
        await Execute(connection, "select 1 from events.events where id=(select event_id from events.rounds where id=$1) for update", ct, roundId);
        await Execute(connection, "select 1 from events.rounds where id=$1 for update", ct, roundId);
        Guid eventId;
        Guid contentId;
        DateTimeOffset starts;
        long seed;
        string state;
        PairingAlgorithmVersion algorithm;
        await using (var query = Command(connection, "select r.event_id,e.experience_version_id,r.starts_at,r.seed,r.state,r.algorithm_version from events.rounds r join events.events e on e.id=r.event_id where r.id=$1 and e.state in ('Published','Running')", roundId))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return;
            }

            eventId = reader.GetGuid(0);
            contentId = reader.GetGuid(1);
            starts = reader.GetFieldValue<DateTimeOffset>(2);
            seed = reader.GetInt64(3);
            state = reader.GetString(4);
            algorithm = (PairingAlgorithmVersion)reader.GetInt32(5);
        }
        var now = Now;
        if (state != "Scheduled" || starts > now)
        {
            return;
        }

        if (now > starts.AddSeconds(60))
        {
            await Execute(connection, "update events.rounds set state='Cancelled',version=version+1 where id=$1", ct, roundId);
            return;
        }
        List<Candidate> candidates = [];
        Dictionary<Guid, string> locales = [];
        foreach (var user in ids)
        {
            if (!await Eligible(connection, user, ct))
            {
                continue;
            }

            await using var query = Command(connection, """
                select rg.checked_in_at,coalesce(u.preferred_locale,'en-US'),p.allow_different_display_locales,
                  array(select language_code from profiles.communication_languages l where l.user_id=u.id order by language_code),
                  (select count(*)::integer from events.rounds earlier where earlier.event_id=rg.event_id and earlier.starts_at < $3
                    and earlier.state in ('Inviting','Closed') and rg.checked_in_at<=earlier.starts_at
                    and not exists(select 1 from events.pairing_participants pp where pp.round_id=earlier.id and pp.user_id=u.id)),
                  location.country_code, regional.mode,
                  array(select country_code from profiles.regional_preference_countries rc where rc.user_id=u.id order by country_code)
                from events.registrations rg join identity.users u on u.id=rg.user_id join profiles.preferences p on p.user_id=u.id
                left join profiles.profiles location on location.user_id=u.id
                left join profiles.regional_preferences regional on regional.user_id=u.id
                where rg.event_id=$1 and rg.user_id=$2 and rg.state='CheckedIn'
                  and not exists(select 1 from events.pairing_participants pp where pp.user_id=u.id and pp.released_at is null)
                """, eventId, user, starts);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                continue;
            }

            var preferences = Preferences.Unrestricted;
            if (algorithm == PairingAlgorithmVersion.GreedyByPriorityV3)
            {
                // Absence is pending, never an implicit unrestricted choice. Only declared country is projected.
                if (reader.IsDBNull(6))
                { continue; }
                Dictionary<string, string> own = [];
                Dictionary<string, IReadOnlySet<string>> accepted = [];
                if (!reader.IsDBNull(5))
                { own.Add("country", reader.GetString(5)); }
                if (reader.GetString(6) == "countries")
                {
                    accepted.Add("country", reader.GetFieldValue<string[]>(7).ToHashSet(StringComparer.Ordinal));
                }
                else if (reader.GetString(6) != "unrestricted")
                {
                    throw new InvalidOperationException("Invalid stored regional mode.");
                }
                preferences = new Preferences(own, accepted);
            }
            var locale = reader.GetString(1);
            candidates.Add(new(user, reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt32(4), preferences,
                new CommunicationDeclaration(locale, reader.GetFieldValue<string[]>(3), reader.GetBoolean(2))));
            locales.Add(user, locale);
        }
        List<UserPair> blocked = [];
        List<UserPair> prior = [];
        await using (var query = Command(connection, "select blocker_id,blocked_id from safety.blocks where blocker_id=any($1) or blocked_id=any($1)", ids.ToArray()))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                blocked.Add(UserPair.Of(reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        await using (var query = Command(connection, "select user_low,user_high from events.pairings where event_id=$1", eventId))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                prior.Add(UserPair.Of(reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        var selected = PairSelector.Select(new(candidates, blocked, prior, new RoundSeed(seed), algorithm));
        RoundsBuilt.Add(1);
        PairsBuilt.Add(selected.Pairs.Count);
        CandidatesWaiting.Add(selected.Unpaired.Count);
        foreach (var pair in selected.Pairs)
        {
            var id = Guid.CreateVersion7();
            var room = Guid.CreateVersion7();
            var agreement = selected.CommunicationAgreements[pair];
            await Execute(connection, """
                insert into events.pairings(id,round_id,event_id,user_low,user_high,algorithm_version,communication_language,communication_policy_version,created_at)
                values($1,$2,$3,$4,$5,$6,$7,$8,$9)
                """, ct, id, roundId, eventId, pair.UserLow, pair.UserHigh, (int)selected.AlgorithmVersion, agreement.Language, agreement.PolicyVersion, now);
            await Execute(connection, "insert into experiences.game_sessions(id,pairing_id,experience_version_id,state,invite_expires_at) values($1,$2,$3,'Invited',$4)", ct, room, id, contentId, now.AddSeconds(60));
            foreach (var user in new[] { pair.UserLow, pair.UserHigh })
            {
                await Execute(connection, "insert into events.pairing_participants(pairing_id,user_id,round_id) values($1,$2,$3)", ct, id, user, roundId);
                await Execute(connection, "insert into experiences.game_participants(game_session_id,user_id,display_locale) values($1,$2,$3)", ct, room, user, locales[user]);
            }
            await ScheduleAsync(connection, "expire-invite", room, now.AddSeconds(60), ct);
        }
        if (ObserveMatchingStage is { } written)
        {
            await written("PairingsWritten", ct);
        }
        await Execute(connection, "update events.rounds set state='Inviting',version=version+1 where id=$1", ct, roundId);
        await Execute(connection, "update events.events set state='Running',version=version+1 where id=$1", ct, eventId);
    }
}
