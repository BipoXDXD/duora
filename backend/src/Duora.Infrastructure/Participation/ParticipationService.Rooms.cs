using System.Text.Json;
using Duora.Core.Experiences;
using Duora.Core.Experiences.Constellation;
using Duora.Core.Experiences.UnlikelyRoutes;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Participation;

public sealed record ParticipationRoom(Guid Id, string State, long Version, string Language, int PolicyVersion,
    string Locale, DateTimeOffset InviteExpiresAt, ConstellationView? Game, IReadOnlyList<LocalizedAudioView> Audio, UnlikelyRoutesView? RoutesGame = null);

public sealed partial class ParticipationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private ExperienceEngine<ConstellationContent, ConstellationState, ConstellationAction, ConstellationView> Engine => new(new ConstellationRules(), new PreciseClock(time));
    private sealed record RoomRow(Guid Id, Guid PairingId, Guid RoundId, Guid EventId, Guid Low, Guid High,
        Guid ContentId, string State, long Version, DateTimeOffset Expires, DateTimeOffset RoundStart,
        string Language, int Policy, string Locale, string? Public, string? Control, string Kind, DateTimeOffset? StartedAt, DateTimeOffset? Deadline);

    private static async Task<LocalizedConstellationContent?> ContentAsync(NpgsqlConnection connection, Guid id, CancellationToken ct)
    {
        ConstellationContent en;
        ConstellationContent pt;
        CatalogAudioManifest[] manifest;
        await using (var query = Command(connection, "select content_en::text,content_pt::text,audio_manifest::text from experiences.experience_versions where id=$1 and editorial_state in ('Published','Retired')", id))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            en = JsonSerializer.Deserialize<ConstellationContent>(reader.GetString(0), Json)!;
            pt = JsonSerializer.Deserialize<ConstellationContent>(reader.GetString(1), Json)!;
            manifest = JsonSerializer.Deserialize<CatalogAudioManifest[]>(reader.GetString(2), Json)!;
        }
        List<LocalizedAudio> audio = [];
        foreach (var cue in manifest)
        {
            audio.Add(new(cue.CueId, await ReadVariant(cue.EnUS, "en-US"), await ReadVariant(cue.PtBR, "pt-BR")));
        }
        return new(en, pt, audio);

        async Task<LocalizedAudioVariant> ReadVariant(CatalogAudioManifestVariant variant, string locale)
        {
            await using var query = Command(connection, "select bytes,sha256 from experiences.audio_assets where experience_version_id=$1 and asset_id=$2 and display_locale=$3", id, variant.AssetId, locale);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(1) != variant.Sha256)
            {
                throw new InvalidOperationException("Pinned audio manifest is incomplete.");
            }

            return ToVariant(new(variant.AssetId, variant.Transcript, variant.AccessibleDescription, variant.Provenance, variant.Sha256, reader.GetFieldValue<byte[]>(0)));
        }
    }

    private static async Task<RoomRow?> LoadRoomAsync(NpgsqlConnection connection, Guid room, Guid user, bool lockRows, CancellationToken ct, bool authorize = true)
    {
        RoomRow row;
        await using (var query = Command(connection, """
            select s.id,s.pairing_id,p.round_id,p.event_id,p.user_low,p.user_high,s.experience_version_id,s.state,s.version,
              s.invite_expires_at,r.starts_at,p.communication_language,p.communication_policy_version,gp.display_locale,
              s.public_state::text,s.control_state::text,ev.experience_kind,s.game_started_at,s.active_deadline_at
            from experiences.game_sessions s join experiences.experience_versions ev on ev.id=s.experience_version_id join events.pairings p on p.id=s.pairing_id
            join events.rounds r on r.id=p.round_id join experiences.game_participants gp on gp.game_session_id=s.id
            where s.id=$1 and gp.user_id=$2 and (gp.departed_at is null or not $3)
            """, room, user, authorize))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            row = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetGuid(6), reader.GetString(7), reader.GetInt64(8), reader.GetFieldValue<DateTimeOffset>(9), reader.GetFieldValue<DateTimeOffset>(10), reader.GetString(11), reader.GetInt32(12), reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17), reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18));
        }
        if (lockRows)
        {
            await Guard(connection, [row.Low, row.High], ct);
            await Execute(connection, "select 1 from events.events where id=$1 for update", ct, row.EventId);
            await Execute(connection, "select 1 from events.rounds where id=$1 for update", ct, row.RoundId);
            await Execute(connection, "select 1 from experiences.game_sessions where id=$1 for update", ct, room);
            return await LoadRoomAsync(connection, room, user, false, ct, authorize);
        }
        if (!authorize)
        {
            return row;
        }
        if (!await Eligible(connection, row.Low, ct) || !await Eligible(connection, row.High, ct))
        {
            return null;
        }

        await using var blocked = Command(connection, "select exists(select 1 from safety.blocks where (blocker_id=$1 and blocked_id=$2) or (blocker_id=$2 and blocked_id=$1))", row.Low, row.High);
        return (bool)(await blocked.ExecuteScalarAsync(ct))! ? null : row;
    }

    public async Task<ParticipationRoom?> RoomAsync(Guid room, Guid user, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var row = await LoadRoomAsync(connection, room, user, false, ct);
        if (row is null)
        {
            return null;
        }

        ConstellationView? game = null;
        UnlikelyRoutesView? routesGame = null;
        IReadOnlyList<LocalizedAudioView> audio = [];
        if (row.Kind == "unlikely-routes" && row.Public is not null && row.State is "Active" or "Completed" or "Expired")
        {
            var content = await RoutesContentAsync(connection, row.ContentId, ct) ?? throw new InvalidOperationException("Pinned catalog missing.");
            routesGame = content.Project(await RestoreRoutesAsync(connection, row, content, ct), new(user), row.Locale);
            if (row.State is "Active" or "Completed")
            {
                audio = content.ProjectAudio(row.Locale);
            }
        }
        else if (row.Public is not null && row.State is "Active" or "Completed" or "Expired")
        {
            var content = await ContentAsync(connection, row.ContentId, ct) ?? throw new InvalidOperationException("Pinned catalog missing.");
            game = content.Project(await RestoreAsync(connection, row, content, ct), new(user), row.Locale);
            if (row.State is "Active" or "Completed")
            {
                audio = content.ProjectAudio(row.Locale);
            }
        }
        await tx.CommitAsync(ct);
        var state = row.State == "Invited" && row.Expires <= Now ? "Expired" : row.State;
        return new(row.Id, state, row.Version, row.Language, row.Policy, row.Locale, row.Expires, game, audio, routesGame);
    }

    public async Task<ParticipationOutcome> RespondAsync(Guid room, Guid user, bool accept, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var row = await LoadRoomAsync(connection, room, user, true, ct);
        if (row is null)
        {
            return ParticipationOutcome.Unavailable;
        }

        var now = Now;
        if (row.State != "Invited")
        {
            return row.State == "Active" && accept ? ParticipationOutcome.Done : ParticipationOutcome.Unavailable;
        }

        if (!accept || row.Expires <= now)
        {
            await ReleaseAsync(connection, row, row.Expires <= now ? "Expired" : "Aborted", ct);
            await tx.CommitAsync(ct);
            return ParticipationOutcome.Done;
        }
        var changed = await Execute(connection, "update experiences.game_participants set accepted_at=$3 where game_session_id=$1 and user_id=$2 and accepted_at is null", ct, room, user, now);
        if (changed == 0)
        {
            return ParticipationOutcome.Done;
        }

        await using var countQuery = Command(connection, "select count(*) from experiences.game_participants where game_session_id=$1 and accepted_at is not null", room);
        var count = (long)(await countQuery.ExecuteScalarAsync(ct))!;
        if (count == 2 && row.Kind == "unlikely-routes")
        {
            await StartRoutesAsync(connection, row, ct);
        }
        else if (count == 2)
        {
            var content = await ContentAsync(connection, row.ContentId, ct) ?? throw new InvalidOperationException("Pinned catalog missing.");
            var session = Engine.CreateSession(content.ForLocale("en-US"), new(new(row.Low), new(row.High)), row.RoundStart, GameTimeLimits.Default, new SessionSeed(0));
            var snapshot = ConstellationSnapshot.Capture(session);
            snapshot = snapshot with { Control = snapshot.Control with { ResourceVersion = row.Version + 1 } };
            await SaveAsync(connection, row, snapshot, ct);
            await ScheduleAsync(connection, "game-deadline", room, snapshot.Control.Deadline, ct);
        }
        else
        {
            await Execute(connection, "update experiences.game_sessions set version=version+1 where id=$1", ct, room);
        }

        await tx.CommitAsync(ct);
        return ParticipationOutcome.Done;
    }

    public async Task<ParticipationOutcome> ActionAsync(Guid room, Guid user, Guid actionId, long expectedVersion, string type, string cardId, string? optionId, CancellationToken ct)
    {
        if (actionId == Guid.Empty || expectedVersion < 1 || string.IsNullOrWhiteSpace(cardId))
        {
            return ParticipationOutcome.Invalid;
        }

        ConstellationAction? action = type switch { "choose-option" when !string.IsNullOrWhiteSpace(optionId) => new ConstellationAction.ChooseOption(cardId, optionId), "skip-card" => new ConstellationAction.SkipCard(cardId), _ => null };
        if (action is null)
        {
            return ParticipationOutcome.Invalid;
        }

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var row = await LoadRoomAsync(connection, room, user, true, ct);
        if (row?.Public is null || row.Kind != "constellation" || row.State is "Aborted" or "Expired")
        {
            return ParticipationOutcome.Unavailable;
        }

        var content = await ContentAsync(connection, row.ContentId, ct) ?? throw new InvalidOperationException("Pinned catalog missing.");
        var session = await RestoreAsync(connection, row, content, ct);
        var result = Engine.ValidateAction(session, new(new(actionId), new(user), new(expectedVersion), action));
        if (result is ActionValidation<ConstellationState, ConstellationAction>.AlreadyApplied)
        {
            return ParticipationOutcome.Done;
        }

        if (result is ActionValidation<ConstellationState, ConstellationAction>.Conflict)
        {
            return ParticipationOutcome.Conflict;
        }

        if (result is not ActionValidation<ConstellationState, ConstellationAction>.Accepted accepted)
        {
            return ParticipationOutcome.Invalid;
        }

        var applied = Engine.ApplyAction(accepted.Action);
        await SaveAsync(connection, row, ConstellationSnapshot.Capture(applied.Session), ct);
        if (applied.Session.Status == SessionStatus.Completed)
        {
            await ReleaseReservationsAsync(connection, row.PairingId, ct);
        }

        await tx.CommitAsync(ct);
        return ParticipationOutcome.Done;
    }

    public async Task<ParticipationOutcome> ExitAsync(Guid room, Guid user, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var row = await LoadRoomAsync(connection, room, user, true, ct, authorize: false);
        if (row is null)
        {
            return ParticipationOutcome.Unavailable;
        }

        if (row.State is "Invited" or "Active")
        {
            await ReleaseAsync(connection, row, "Aborted", ct);
        }

        await Execute(connection, "update experiences.game_participants set departed_at=coalesce(departed_at,$3) where game_session_id=$1 and user_id=$2", ct, room, user, Now);
        await tx.CommitAsync(ct);
        return ParticipationOutcome.Done;
    }

    private static async Task<GameSession<ConstellationState, ConstellationAction>> RestoreAsync(NpgsqlConnection connection, RoomRow row, LocalizedConstellationContent content, CancellationToken ct)
    {
        List<ConstellationSnapshotPrivate> privateStates = [];
        List<ConstellationSnapshotAction> actions = [];
        await using (var query = Command(connection, "select private_state::text from experiences.game_participants where game_session_id=$1 order by user_id", row.Id))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                privateStates.Add(JsonSerializer.Deserialize<ConstellationSnapshotPrivate>(reader.GetString(0), Json)!);
            }
        }

        await using (var query = Command(connection, "select actor_id,action_id,type,expected_version,applied_version,payload_sha256 from experiences.game_actions where game_session_id=$1", row.Id))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                actions.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5)));
            }
        }

        return new ConstellationSnapshot(1, JsonSerializer.Deserialize<ConstellationSnapshotPublic>(row.Public!, Json)!, privateStates,
            JsonSerializer.Deserialize<ConstellationSnapshotControl>(row.Control!, Json)!, actions).Restore(content.ForLocale("en-US"));
    }

    private static async Task SaveAsync(NpgsqlConnection connection, RoomRow row, ConstellationSnapshot snapshot, CancellationToken ct)
    {
        var changed = await Execute(connection, """
            update experiences.game_sessions set state=$2,version=$3,game_started_at=$4,active_deadline_at=$5,public_state=$6::jsonb,control_state=$7::jsonb where id=$1 and version=$8
            """, ct, row.Id, snapshot.Control.Status.ToString(), snapshot.Control.ResourceVersion, snapshot.Control.StartedAt, snapshot.Control.Deadline,
            JsonSerializer.Serialize(snapshot.PublicState, Json), JsonSerializer.Serialize(snapshot.Control, Json), row.Version);
        if (changed != 1)
        {
            throw new InvalidOperationException("Session version changed under lock.");
        }
        foreach (var item in snapshot.PrivateStates)
        {
            await Execute(connection, "update experiences.game_participants set private_state=$3::jsonb where game_session_id=$1 and user_id=$2", ct, row.Id, item.Participant, JsonSerializer.Serialize(item, Json));
        }

        foreach (var action in snapshot.Actions)
        {
            await Execute(connection, """
                insert into experiences.game_actions(game_session_id,actor_id,action_id,type,expected_version,applied_version,payload_sha256)
                values($1,$2,$3,$4,$5,$6,$7) on conflict do nothing
                """, ct, row.Id, action.Actor, action.ActionId, action.Type, action.ExpectedVersion, action.ResultingVersion, action.Sha256);
        }
    }

    private async Task ReleaseAsync(NpgsqlConnection connection, RoomRow row, string state, CancellationToken ct)
    {
        await Execute(connection, "update experiences.game_sessions set state=$2,version=version+1 where id=$1 and state in ('Invited','Active')", ct, row.Id, state);
        await ReleaseReservationsAsync(connection, row.PairingId, ct);
    }

    private async Task ReleaseReservationsAsync(NpgsqlConnection connection, Guid pairing, CancellationToken ct)
    {
        await Execute(connection, "update events.pairings set released_at=coalesce(released_at,$2),release_reason='Finished' where id=$1", ct, pairing, Now);
        await Execute(connection, "update events.pairing_participants set released_at=coalesce(released_at,$2) where pairing_id=$1", ct, pairing, Now);
    }
}
