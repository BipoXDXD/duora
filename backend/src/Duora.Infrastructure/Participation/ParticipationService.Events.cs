using System.Diagnostics.Metrics;
using Duora.Core.Matching;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
public sealed partial class ParticipationService(NpgsqlDataSource source, TimeProvider time, IOptions<ParticipationOptions>? options = null, ILogger<ParticipationService>? logger = null)
{
    private static readonly Meter MatchingMeter = new("Duora.Matching");
    private static readonly Counter<long> RoundsBuilt = MatchingMeter.CreateCounter<long>("duora.matching.rounds");
    private static readonly Counter<long> PairsBuilt = MatchingMeter.CreateCounter<long>("duora.matching.pairs");
    private static readonly Counter<long> CandidatesWaiting = MatchingMeter.CreateCounter<long>("duora.matching.waiting_candidates");
    private static readonly HashSet<string> EventTimeZones = TimeZoneInfo.GetSystemTimeZones()
        .Where(zone => zone.HasIanaId).Select(zone => zone.Id).Append("UTC").ToHashSet(StringComparer.Ordinal);
    /// <summary>As três rodadas e o fechamento somam 36 min depois do início; a folga cobre o arredondamento do PostgreSQL.</summary>
    private static readonly DateTimeOffset LatestEventStart = DateTimeOffset.MaxValue.AddHours(-1);
    /// <summary>Depois deste atraso sobre o início, a rodada é cancelada em vez de montada.</summary>
    private static readonly TimeSpan LateBuildLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InviteWindow = TimeSpan.FromSeconds(60);
    private bool MatchingEnabled => options?.Value.MatchingEnabled ?? true;

    private DateTimeOffset Now => PreciseClock.Truncate(time.GetUtcNow());
    private sealed class PreciseClock(TimeProvider inner) : TimeProvider
    {
        public static DateTimeOffset Truncate(DateTimeOffset instant) => new(instant.Ticks - instant.Ticks % 10, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Truncate(inner.GetUtcNow());
    }

    /// <summary>Para o motor do jogo decidir no mesmo instante que já decidiu a conclusão do job, sem reler o relógio.</summary>
    private sealed class FrozenClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
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
        // A cutover pause must also stop new rounds from being labelled by an old publisher.
        // Requests already in flight still require the operator's explicit drain.
        if (!MatchingEnabled)
        {
            return null;
        }

        // O Npgsql só grava offset 0, e somar a agenda no relógio local de um offset alto estoura mesmo com o instante válido.
        var startsAtUtc = startsAt.ToUniversalTime();
        if (!IsPublishable(title, startsAtUtc, capacity, timeZone))
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
        await Execute(connection, "insert into events.events(id,title,state,starts_at,capacity,experience_version_id,time_zone) values($1,$2,'Published',$3,$4,$5,$6)", ct, id, title, startsAtUtc, capacity, contentId, (object?)timeZone ?? DBNull.Value);
        for (var number = 1; number <= 3; number++)
        {
            var round = Guid.CreateVersion7();
            var start = startsAtUtc.AddMinutes((number - 1) * 12);
            var algorithm = options?.Value.RegionalMatchingStartsAt is { } cutover && start >= cutover
                ? PairingAlgorithmVersion.GreedyByPriorityV3 : PairingAlgorithmVersion.GreedyByPriorityV2;
            await Execute(connection, "insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed,algorithm_version) values($1,$2,$3,'Scheduled',$4,$5,$6,$7)", ct, round, id, number, start, start.AddMinutes(12), Random.Shared.NextInt64(), (int)algorithm);
            await ScheduleAsync(connection, "build-round", round, start, ct);
            await ScheduleAsync(connection, "close-round", round, start.AddMinutes(12), ct);
        }
        await transaction.CommitAsync(ct);
        return id;
    }

    private bool IsPublishable(string title, DateTimeOffset startsAtUtc, int capacity, string? timeZone) =>
        (timeZone is null || EventTimeZones.Contains(timeZone))
        && !string.IsNullOrWhiteSpace(title) && title.Length <= 120 && !title.Any(char.IsControl)
        && capacity is >= 1 and <= 100
        && startsAtUtc > Now.AddMinutes(10) && startsAtUtc <= LatestEventStart;

    public async Task<ParticipationOutcome> RegisterAsync(Guid eventId, Guid user, bool checkIn, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await RegisterOnceAsync(eventId, user, checkIn, ct);
            }
            catch (RegistrationPlanChangedException)
            {
                // A new waiter appeared before the event lock. Start over with all guards, in UUID order.
            }
        }
        throw new AccountBusyException(TimeSpan.FromSeconds(2));
    }

    private async Task<ParticipationOutcome> RegisterOnceAsync(Guid eventId, Guid user, bool checkIn, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var plan = await RegistrationLockPlan.ForRegistrationAsync(connection, eventId, user, ct);
        await Guard(connection, plan.Users, ct);
        await plan.LockEventsAsync(connection, ct);
        plan.RequireCovered(await RegistrationLockPlan.ForRegistrationAsync(connection, eventId, user, ct));
        if (!await Eligible(connection, user, ct))
        {
            return ParticipationOutcome.Unavailable;
        }

        if (!checkIn)
        {
            await using var existing = Command(connection,
                "select state from events.registrations where event_id=$1 and user_id=$2", eventId, user);
            if (await existing.ExecuteScalarAsync(ct) is string existingState
                && existingState is not ("Confirmed" or "CheckedIn" or "Waitlisted"))
            {
                return ParticipationOutcome.Unavailable;
            }
            await RegistrationTransitions.PromoteAsync(connection, eventId, Now, ct);
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

            var changed = await Execute(connection, "update events.registrations set state='CheckedIn',checked_in_at=coalesce(checked_in_at,$3),version=version+case when state='Confirmed' then 1 else 0 end where event_id=$1 and user_id=$2 and state in ('Confirmed','CheckedIn')", ct, eventId, user, now);
            if (changed == 0)
            {
                return ParticipationOutcome.Unavailable;
            }
        }
        else
        {
            var inserted = await Execute(connection, """
                insert into events.registrations(event_id,user_id,state,waitlist_position,registered_at,confirmed_at)
                values($1,$2,$3,$4,$5,case when $3='Confirmed' then $5 else null end) on conflict(event_id,user_id) do nothing
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

        // Só a sala mais recente conta: filtrar dentro do lateral faria a sala abortada trazer de volta a de uma rodada anterior.
        await using var command = Command(connection, """
            select r.state,case when s.state<>'Aborted' and not s.blocked then s.id end,s.state from events.registrations r
            left join lateral(select gs.id,gs.state,
                exists(select 1 from safety.blocks b where (b.blocker_id=p.user_low and b.blocked_id=p.user_high) or (b.blocker_id=p.user_high and b.blocked_id=p.user_low)) blocked
              from events.pairings p join experiences.game_sessions gs on gs.pairing_id=p.id
              where p.event_id=r.event_id and (p.user_low=r.user_id or p.user_high=r.user_id)
              order by p.created_at desc limit 1) s on true
            where r.event_id=$1 and r.user_id=$2
            """, eventId, user);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new("Unregistered", null);
        }

        return reader.IsDBNull(1) ? new(reader.GetString(0) == "CheckedIn" ? "Waiting" : reader.GetString(0), null)
            : new(reader.GetString(2) == "Expired" ? "Unavailable" : reader.GetString(2), reader.GetGuid(1));
    }

    public async Task BuildRoundPairingsAsync(Guid roundId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await BuildRoundAsync(connection, roundId, Now, ct);
        await transaction.CommitAsync(ct);
    }

    private async Task BuildRoundAsync(NpgsqlConnection connection, Guid roundId, DateTimeOffset now, CancellationToken ct)
    {
        if (!MatchingEnabled)
        {
            return;
        }

        // Preliminary IDs are suggestions only. The same set is revalidated after acquiring its user guards.
        var ids = await ReadCheckedInUsersAsync(connection, roundId, ct);
        await ObserveStageAsync("CandidatesRead", ct);
        await Guard(connection, ids, ct);
        await ObserveStageAsync("CandidatesGuarded", ct);
        await LockEventAndRoundAsync(connection, roundId, ct);
        if (await ReadRoundToBuildAsync(connection, roundId, ct) is not { } round)
        {
            return;
        }

        if (round.State != "Scheduled" || round.Starts > now)
        {
            return;
        }

        if (now > round.Starts + LateBuildLimit)
        {
            await Execute(connection, "update events.rounds set state='Cancelled',version=version+1 where id=$1", ct, round.Id);
            return;
        }

        var eligible = await LoadCandidatesAsync(connection, ids, round, ct);
        var blocked = await ReadPairsAsync(connection, "select blocker_id,blocked_id from safety.blocks where blocker_id=any($1) or blocked_id=any($1)", ids.ToArray(), ct);
        var prior = await ReadPairsAsync(connection, "select user_low,user_high from events.pairings where event_id=$1", round.EventId, ct);
        var selected = PairSelector.Select(new(eligible.Candidates, blocked, prior, new RoundSeed(round.Seed), round.Algorithm));
        RecordMetrics(selected);
        await WritePairingsAsync(connection, round, selected, eligible.DisplayLocales, now, ct);
        await ObserveStageAsync("PairingsWritten", ct);
        await Execute(connection, "update events.rounds set state='Inviting',version=version+1 where id=$1", ct, round.Id);
        await Execute(connection, "update events.events set state='Running',version=version+1 where id=$1", ct, round.EventId);
    }

    private static async Task<List<Guid>> ReadCheckedInUsersAsync(NpgsqlConnection connection, Guid roundId, CancellationToken ct)
    {
        List<Guid> ids = [];
        await using var query = Command(connection, "select rg.user_id from events.registrations rg join events.rounds r on r.event_id=rg.event_id where r.id=$1 and rg.state='CheckedIn' order by rg.user_id", roundId);
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private Task ObserveStageAsync(string stage, CancellationToken ct) =>
        ObserveMatchingStage?.Invoke(stage, ct) ?? Task.CompletedTask;

    private static async Task LockEventAndRoundAsync(NpgsqlConnection connection, Guid roundId, CancellationToken ct)
    {
        await Execute(connection, "select 1 from events.events where id=(select event_id from events.rounds where id=$1) for update", ct, roundId);
        await Execute(connection, "select 1 from events.rounds where id=$1 for update", ct, roundId);
    }

    private static async Task<RoundToBuild?> ReadRoundToBuildAsync(NpgsqlConnection connection, Guid roundId, CancellationToken ct)
    {
        await using var query = Command(connection, "select r.event_id,e.experience_version_id,r.starts_at,r.seed,r.state,r.algorithm_version from events.rounds r join events.events e on e.id=r.event_id where r.id=$1 and e.state in ('Published','Running')", roundId);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new(roundId, reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt64(3),
            reader.GetString(4), (PairingAlgorithmVersion)reader.GetInt32(5));
    }

    private static async Task<EligibleCandidates> LoadCandidatesAsync(NpgsqlConnection connection, IEnumerable<Guid> ids, RoundToBuild round, CancellationToken ct)
    {
        List<Candidate> candidates = [];
        Dictionary<Guid, string> locales = [];
        foreach (var user in ids)
        {
            if (!await Eligible(connection, user, ct))
            {
                continue;
            }

            if (await LoadCandidateAsync(connection, user, round, ct) is not { } loaded)
            {
                continue;
            }

            candidates.Add(loaded.Candidate);
            locales.Add(user, loaded.DisplayLocale);
        }

        return new(candidates, locales);
    }

    /// <summary>Null when the user is no longer a checked-in participant of the event, is already reserved elsewhere or has no regional choice yet.</summary>
    private static async Task<LoadedCandidate?> LoadCandidateAsync(NpgsqlConnection connection, Guid user, RoundToBuild round, CancellationToken ct)
    {
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
            """, round.EventId, user, round.Starts);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        if (ReadRegionalPreferences(reader, round.Algorithm) is not { } preferences)
        {
            return null;
        }

        var locale = reader.GetString(1);
        var communication = new CommunicationDeclaration(locale, reader.GetFieldValue<string[]>(3), reader.GetBoolean(2));
        return new(new Candidate(user, reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt32(4), preferences, communication), locale);
    }

    /// <summary>Null when a V3 round finds no declared regional mode: absence is pending, never an implicit unrestricted choice.</summary>
    private static Preferences? ReadRegionalPreferences(NpgsqlDataReader reader, PairingAlgorithmVersion algorithm)
    {
        if (algorithm != PairingAlgorithmVersion.GreedyByPriorityV3)
        {
            return Preferences.Unrestricted;
        }

        // Only declared country is projected.
        if (reader.IsDBNull(6))
        {
            return null;
        }

        Dictionary<string, string> own = [];
        Dictionary<string, IReadOnlySet<string>> accepted = [];
        if (!reader.IsDBNull(5))
        {
            own.Add("country", reader.GetString(5));
        }

        if (reader.GetString(6) == "countries")
        {
            accepted.Add("country", reader.GetFieldValue<string[]>(7).ToHashSet(StringComparer.Ordinal));
        }
        else if (reader.GetString(6) != "unrestricted")
        {
            throw new InvalidOperationException("Invalid stored regional mode.");
        }

        return new Preferences(own, accepted);
    }

    private static async Task<List<UserPair>> ReadPairsAsync(NpgsqlConnection connection, string sql, object parameter, CancellationToken ct)
    {
        List<UserPair> pairs = [];
        await using var query = Command(connection, sql, parameter);
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            pairs.Add(UserPair.Of(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return pairs;
    }

    private static void RecordMetrics(PairSelection selected)
    {
        RoundsBuilt.Add(1);
        PairsBuilt.Add(selected.Pairs.Count);
        CandidatesWaiting.Add(selected.Unpaired.Count);
    }

    private static async Task WritePairingsAsync(NpgsqlConnection connection, RoundToBuild round, PairSelection selected,
        IReadOnlyDictionary<Guid, string> locales, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var pair in selected.Pairs)
        {
            await WritePairingAsync(connection, round, selected, pair, locales, now, ct);
        }
    }

    private static async Task WritePairingAsync(NpgsqlConnection connection, RoundToBuild round, PairSelection selected, UserPair pair,
        IReadOnlyDictionary<Guid, string> locales, DateTimeOffset now, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var room = Guid.CreateVersion7();
        var agreement = selected.CommunicationAgreements[pair];
        var inviteExpiresAt = now + InviteWindow;
        await Execute(connection, """
            insert into events.pairings(id,round_id,event_id,user_low,user_high,algorithm_version,communication_language,communication_policy_version,created_at)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9)
            """, ct, id, round.Id, round.EventId, pair.UserLow, pair.UserHigh, (int)selected.AlgorithmVersion, agreement.Language, agreement.PolicyVersion, now);
        await Execute(connection, "insert into experiences.game_sessions(id,pairing_id,experience_version_id,state,invite_expires_at) values($1,$2,$3,'Invited',$4)", ct, room, id, round.ContentId, inviteExpiresAt);
        foreach (var user in new[] { pair.UserLow, pair.UserHigh })
        {
            await Execute(connection, "insert into events.pairing_participants(pairing_id,user_id,round_id) values($1,$2,$3)", ct, id, user, round.Id);
            await Execute(connection, "insert into experiences.game_participants(game_session_id,user_id,display_locale) values($1,$2,$3)", ct, room, user, locales[user]);
        }

        await ScheduleAsync(connection, "expire-invite", room, inviteExpiresAt, ct);
    }

    private sealed record RoundToBuild(Guid Id, Guid EventId, Guid ContentId, DateTimeOffset Starts, long Seed, string State, PairingAlgorithmVersion Algorithm);

    private sealed record LoadedCandidate(Candidate Candidate, string DisplayLocale);

    private sealed record EligibleCandidates(IReadOnlyList<Candidate> Candidates, IReadOnlyDictionary<Guid, string> DisplayLocales);
}
