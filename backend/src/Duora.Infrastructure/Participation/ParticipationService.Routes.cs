using System.Security.Cryptography;
using System.Text.Json;
using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Participation;

public sealed partial class ParticipationService
{
    private ExperienceEngine<UnlikelyRoutesContent, UnlikelyRoutesState, UnlikelyRoutesAction, UnlikelyRoutesView> RoutesEngine => new(new UnlikelyRoutesRules(new UnlikelyRoutesSettings(1)), new PreciseClock(time));

    private async Task StartRoutesAsync(NpgsqlConnection connection, RoomRow row, CancellationToken ct)
    {
        var content = await RoutesContentAsync(connection, row.ContentId, ct) ?? throw new InvalidOperationException("Pinned catalog missing.");
        var session = RoutesEngine.CreateSession(content.ForLocale("en-US"), new(new(row.Low), new(row.High)), row.RoundStart, GameTimeLimits.Default, new SessionSeed(RandomNumberGenerator.GetInt32(int.MaxValue)));
        var snapshot = UnlikelyRoutesSnapshot.Capture(session);
        snapshot = snapshot with { Control = snapshot.Control with { ResourceVersion = row.Version + 1 } };
        await SaveRoutesAsync(connection, row, snapshot, ct);
        await ScheduleAsync(connection, "game-deadline", row.Id, snapshot.Control.Deadline, ct);
    }

    public async Task<ParticipationOutcome> RoutesActionAsync(Guid room, Guid user, Guid actionId, long expectedVersion,
        string type, string? stage, string? stepId, string? optionId, CancellationToken ct)
    {
        if (actionId == Guid.Empty || expectedVersion < 1)
        {
            return ParticipationOutcome.Invalid;
        }
        UnlikelyRoutesAction? action = type switch
        {
            "acknowledge" when stage == "Introduction" && stepId is null && optionId is null => new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction),
            "acknowledge" when stage == "Clues" && stepId is null && optionId is null => new UnlikelyRoutesAction.Acknowledge(RoutesStage.Clues),
            "choose-option" when stage is null && !string.IsNullOrWhiteSpace(stepId) && !string.IsNullOrWhiteSpace(optionId) => new UnlikelyRoutesAction.ChooseOption(stepId, optionId),
            "confirm-route" when stage is null && stepId is null && optionId is null => new UnlikelyRoutesAction.ConfirmRoute(),
            "request-revision" when stage is null && stepId is null && optionId is null => new UnlikelyRoutesAction.RequestRevision(),
            _ => null,
        };
        if (action is null)
        {
            return ParticipationOutcome.Invalid;
        }
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var row = await LoadRoomAsync(connection, room, user, true, ct);
        if (row?.Public is null || row.Kind != "unlikely-routes" || row.State is "Aborted" or "Expired")
        {
            return ParticipationOutcome.Unavailable;
        }
        var content = await RoutesContentAsync(connection, row.ContentId, ct) ?? throw new InvalidOperationException("Pinned catalog missing.");
        var session = await RestoreRoutesAsync(connection, row, content, ct);
        var result = RoutesEngine.ValidateAction(session, new(new(actionId), new(user), new(expectedVersion), action));
        if (result is ActionValidation<UnlikelyRoutesState, UnlikelyRoutesAction>.AlreadyApplied)
        {
            return ParticipationOutcome.Done;
        }
        if (result is ActionValidation<UnlikelyRoutesState, UnlikelyRoutesAction>.Conflict)
        {
            return ParticipationOutcome.Conflict;
        }
        if (result is not ActionValidation<UnlikelyRoutesState, UnlikelyRoutesAction>.Accepted accepted)
        {
            return ParticipationOutcome.Invalid;
        }
        var applied = RoutesEngine.ApplyAction(accepted.Action);
        await SaveRoutesAsync(connection, row, UnlikelyRoutesSnapshot.Capture(applied.Session), ct);
        if (applied.Session.Status == SessionStatus.Completed)
        {
            await ReleaseReservationsAsync(connection, row.PairingId, ct);
        }
        await tx.CommitAsync(ct);
        return ParticipationOutcome.Done;
    }

    private async Task ResolveRoutesDeadlineAsync(NpgsqlConnection connection, RoomRow row, DateTimeOffset due, CancellationToken ct)
    {
        var content = await RoutesContentAsync(connection, row.ContentId, ct) ?? throw new InvalidOperationException("Pinned catalog missing.");
        var session = await RestoreRoutesAsync(connection, row, content, ct);
        if (session.Schedule.Deadline == due && RoutesEngine.HandleDeadline(session, session.Version) is DeadlineOutcome<UnlikelyRoutesState, UnlikelyRoutesAction>.Resolved resolved)
        {
            await SaveRoutesAsync(connection, row, UnlikelyRoutesSnapshot.Capture(resolved.Session), ct);
            await ReleaseReservationsAsync(connection, row.PairingId, ct);
        }
    }

    private static async Task<GameSession<UnlikelyRoutesState, UnlikelyRoutesAction>> RestoreRoutesAsync(NpgsqlConnection connection, RoomRow row, LocalizedUnlikelyRoutesContent content, CancellationToken ct)
    {
        List<UnlikelyRoutesSnapshotPrivate> privateStates = [];
        List<UnlikelyRoutesSnapshotAction> actions = [];
        await using (var query = Command(connection, "select private_state::text from experiences.game_participants where game_session_id=$1 order by user_id", row.Id))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                privateStates.Add(JsonSerializer.Deserialize<UnlikelyRoutesSnapshotPrivate>(reader.GetString(0), Json)!);
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

        if (row.StartedAt is null || row.Deadline is null || !Enum.TryParse<SessionStatus>(row.State, out var status))
        {
            throw new InvalidOperationException("Room has no restorable game state.");
        }
        // Columns coordinate jobs and commands and are authoritative (ADR-0020).
        var control = JsonSerializer.Deserialize<UnlikelyRoutesSnapshotControl>(row.Control!, Json)!;
        control = control with
        {
            First = row.Low,
            Second = row.High,
            ResourceVersion = row.Version,
            Status = status,
            StartedAt = row.StartedAt.Value,
            Deadline = row.Deadline.Value,
        };
        return new UnlikelyRoutesSnapshot(1, JsonSerializer.Deserialize<UnlikelyRoutesSnapshotPublic>(row.Public!, Json)!, privateStates,
            control, actions).Restore(content.ForLocale("en-US"));
    }

    private static async Task SaveRoutesAsync(NpgsqlConnection connection, RoomRow row, UnlikelyRoutesSnapshot snapshot, CancellationToken ct)
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

}
