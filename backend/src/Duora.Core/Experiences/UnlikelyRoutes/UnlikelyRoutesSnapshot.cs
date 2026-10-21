using System.Collections.Immutable;

namespace Duora.Core.Experiences.UnlikelyRoutes;

/// <summary>Persistence-only state. Private clue assignments must be stored separately (ADR-0020).</summary>
public sealed record UnlikelyRoutesSnapshot(
    int SnapshotVersion,
    UnlikelyRoutesSnapshotPublic PublicState,
    IReadOnlyList<UnlikelyRoutesSnapshotPrivate> PrivateStates,
    UnlikelyRoutesSnapshotControl Control,
    IReadOnlyList<UnlikelyRoutesSnapshotAction> Actions)
{
    public static UnlikelyRoutesSnapshot Capture(GameSession<UnlikelyRoutesState, UnlikelyRoutesAction> session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = session.State;
        return new(1,
            new(state.Shared.Stage, state.Shared.ChosenOptions.Select((option, index) => new UnlikelyRoutesSnapshotChoice(state.Control.Content.Stops[index].StepId, option.OptionId)).ToArray(),
                state.Shared.StageDoneBy.Select(participant => participant.Value).ToArray(), state.Shared.RevisionsUsed),
            state.PrivateByParticipant.Select(item => new UnlikelyRoutesSnapshotPrivate(item.Key.Value,
                state.Control.Content.ClueSets[0].SequenceEqual(item.Value.Clues) ? 0 : 1)).ToArray(),
            new(session.Experience.RulesVersion, session.Experience.ContentVersion, session.Experience.SchemaVersion,
                session.Participants.First.Value, session.Participants.Second.Value, session.Schedule.StartedAt,
                session.Schedule.Deadline, session.Status, session.Version.Value,
                state.Control.FirstChooser.Value, state.Control.MaxRevisions),
            session.ActionLog.Values.Select(action => new UnlikelyRoutesSnapshotAction(action.Actor.Value, action.ActionId.Value,
                action.Type.Value, action.ExpectedVersion.Value, action.ResultingVersion.Value, action.Fingerprint.Sha256Hex)).ToArray());
    }

    public GameSession<UnlikelyRoutesState, UnlikelyRoutesAction> Restore(UnlikelyRoutesContent pinnedContent)
    {
        ArgumentNullException.ThrowIfNull(pinnedContent);
        ArgumentNullException.ThrowIfNull(PublicState);
        ArgumentNullException.ThrowIfNull(PrivateStates);
        ArgumentNullException.ThrowIfNull(Control);
        ArgumentNullException.ThrowIfNull(Actions);
        ArgumentNullException.ThrowIfNull(PublicState.ChosenOptions);
        ArgumentNullException.ThrowIfNull(PublicState.StageDoneBy);
        var descriptor = new UnlikelyRoutesRules(new(Control.MaxRevisions)).Descriptor;
        if (SnapshotVersion != 1 || Control.RulesVersion != descriptor.RulesVersion
            || Control.SchemaVersion != descriptor.SchemaVersion || Control.ContentVersion != pinnedContent.ContentVersion
            || !Enum.IsDefined(Control.Status) || !Enum.IsDefined(PublicState.Stage))
        {
            throw new ArgumentException("Unsupported snapshot or pinned content version.");
        }

        var participants = new ParticipantPair(new(Control.First), new(Control.Second));
        var participantIds = new[] { Control.First, Control.Second };
        if (!participants.Contains(new(Control.FirstChooser)) || PrivateStates.Count != 2
            || !PrivateStates.Select(item => item.Participant).Order().SequenceEqual(participantIds.Order())
            || !PrivateStates.Select(item => item.ClueSetIndex).Order().SequenceEqual([0, 1])
            || PublicState.StageDoneBy.Any(id => !participantIds.Contains(id))
            || PublicState.StageDoneBy.Distinct().Count() != PublicState.StageDoneBy.Count)
        {
            throw new ArgumentException("Invalid participant or private clue assignment.");
        }

        var chosenCount = PublicState.ChosenOptions.Count;
        var stage = PublicState.Stage;
        if (PublicState.RevisionsUsed < 0 || PublicState.RevisionsUsed > Control.MaxRevisions
            || chosenCount > pinnedContent.Stops.Count || PublicState.StageDoneBy.Count > 1
            || (stage is RoutesStage.Introduction or RoutesStage.Clues && (chosenCount != 0 || PublicState.RevisionsUsed != 0))
            || (stage == RoutesStage.Choices && (chosenCount == pinnedContent.Stops.Count || PublicState.StageDoneBy.Count != 0))
            || (stage is RoutesStage.Adjustment or RoutesStage.Conclusion && chosenCount != pinnedContent.Stops.Count)
            || (stage == RoutesStage.Conclusion && PublicState.StageDoneBy.Count != 0)
            || (Control.Status == SessionStatus.Completed) != (stage == RoutesStage.Conclusion))
        {
            throw new ArgumentException("Inconsistent stage, status, route or revision count.");
        }

        var choices = PublicState.ChosenOptions.Select((option, index) =>
        {
            var stop = pinnedContent.Stops[index];
            if (stop.StepId != option.StepId)
            {
                throw new ArgumentException("Snapshot route stop order mismatch.");
            }

            return stop.FindOption(option.OptionId) ?? throw new ArgumentException("Unknown option for route stop.");
        }).ToImmutableList();
        var privateStates = PrivateStates.ToImmutableDictionary(item => new ParticipantId(item.Participant),
            item => new RoutesPrivateState([.. pinnedContent.ClueSets[item.ClueSetIndex]]));
        // Four acknowledgements; each route attempt permits its choices and at most two adjustment actions.
        var maxActions = 4 + ((pinnedContent.Stops.Count + 2) * (Control.MaxRevisions + 1));
        if (Actions.Count > maxActions || Actions.Select(item => item.ResultingVersion).Distinct().Count() != Actions.Count)
        {
            throw new ArgumentException("Invalid action log size or duplicate resulting version.");
        }

        var actions = Actions.ToImmutableDictionary(item => new ActionId(item.ActionId), item =>
        {
            if (!participants.Contains(new(item.Actor)) || item.ExpectedVersion < 1
                || item.ResultingVersion > Control.ResourceVersion || item.ExpectedVersion >= item.ResultingVersion
                || item.ResultingVersion - item.ExpectedVersion != 1 || !descriptor.ActionTypes.Contains(new ActionType(item.Type)))
            {
                throw new ArgumentException("Invalid action receipt.");
            }

            return new RecordedAction(new(item.Actor), new(item.ActionId), new(item.Type), new(item.ExpectedVersion),
                new(item.ResultingVersion), ActionFingerprint.Restore(item.Sha256));
        });
        var state = new UnlikelyRoutesState(
            new(stage, choices, PublicState.StageDoneBy.Select(id => new ParticipantId(id)).ToImmutableHashSet(), PublicState.RevisionsUsed),
            privateStates, new(pinnedContent, participants, new(Control.FirstChooser), Control.MaxRevisions));
        return new(descriptor.WithContent(pinnedContent.ContentVersion), participants,
            GameSchedule.Restore(Control.StartedAt, Control.Deadline), Control.Status, new(Control.ResourceVersion), state, actions);
    }
}

public sealed record UnlikelyRoutesSnapshotPublic(RoutesStage Stage, IReadOnlyList<UnlikelyRoutesSnapshotChoice> ChosenOptions, IReadOnlyList<Guid> StageDoneBy, int RevisionsUsed);
public sealed record UnlikelyRoutesSnapshotPrivate(Guid Participant, int ClueSetIndex);
public sealed record UnlikelyRoutesSnapshotControl(int RulesVersion, int ContentVersion, int SchemaVersion,
    Guid First, Guid Second, DateTimeOffset StartedAt, DateTimeOffset Deadline, SessionStatus Status, long ResourceVersion,
    Guid FirstChooser, int MaxRevisions);
public sealed record UnlikelyRoutesSnapshotAction(Guid Actor, Guid ActionId, string Type, long ExpectedVersion, long ResultingVersion, string Sha256);

public sealed record UnlikelyRoutesSnapshotChoice(string StepId, string OptionId);
