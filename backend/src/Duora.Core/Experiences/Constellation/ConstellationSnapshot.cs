using System.Collections.Immutable;

namespace Duora.Core.Experiences.Constellation;

/// <summary>Persistence-only DTO, never a participant response. Private states must be stored separately (ADR-0020).</summary>
public sealed record ConstellationSnapshot(
    int SnapshotVersion,
    ConstellationSnapshotPublic PublicState,
    IReadOnlyList<ConstellationSnapshotPrivate> PrivateStates,
    ConstellationSnapshotControl Control,
    IReadOnlyList<ConstellationSnapshotAction> Actions)
{
    public static ConstellationSnapshot Capture(GameSession<ConstellationState, ConstellationAction> session)
    {
        var state = session.State;
        return new(1,
            new(state.Shared.Stage, state.Shared.Outcomes.Select(outcome => outcome switch
            {
                CardOutcome.Revealed revealed => new ConstellationSnapshotOutcome(outcome.Card.CardId,
                    revealed.ChoiceByParticipant.ToDictionary(item => item.Key.Value, item => item.Value.OptionId)),
                _ => new ConstellationSnapshotOutcome(outcome.Card.CardId, null),
            }).ToArray()),
            state.PrivateByParticipant.Select(item => new ConstellationSnapshotPrivate(item.Key.Value,
                item.Value.Answer is CardAnswer.Skipped,
                (item.Value.Answer as CardAnswer.Chosen)?.Option.OptionId)).ToArray(),
            new(session.Experience.RulesVersion, session.Experience.ContentVersion, session.Experience.SchemaVersion,
                session.Participants.First.Value, session.Participants.Second.Value, session.Schedule.StartedAt,
                session.Schedule.Deadline, session.Status, session.Version.Value),
            session.ActionLog.Values.Select(action => new ConstellationSnapshotAction(action.Actor.Value, action.ActionId.Value,
                action.Type.Value, action.ExpectedVersion.Value, action.ResultingVersion.Value, action.Fingerprint.Sha256Hex)).ToArray());
    }

    public GameSession<ConstellationState, ConstellationAction> Restore(ConstellationContent pinnedContent)
    {
        ArgumentNullException.ThrowIfNull(pinnedContent);
        var descriptor = new ConstellationRules().Descriptor;
        if (SnapshotVersion != 1 || Control.RulesVersion != descriptor.RulesVersion
            || Control.SchemaVersion != descriptor.SchemaVersion || Control.ContentVersion != pinnedContent.ContentVersion
            || !Enum.IsDefined(Control.Status) || !Enum.IsDefined(PublicState.Stage))
        {
            throw new ArgumentException("Unsupported snapshot or pinned content version.");
        }

        var participants = new ParticipantPair(new(Control.First), new(Control.Second));
        var cards = pinnedContent.Cards;
        if (PublicState.Outcomes.Count > cards.Count || PrivateStates.Count != 2
            || !PrivateStates.Select(item => item.Participant).Order().SequenceEqual(new[] { Control.First, Control.Second }.Order()))
        {
            throw new ArgumentException("Invalid snapshot participants or outcome count.");
        }

        if ((Control.Status == SessionStatus.Active && (PublicState.Stage != ConstellationStage.Cards || PublicState.Outcomes.Count == cards.Count))
            || (Control.Status != SessionStatus.Active && (PublicState.Stage != ConstellationStage.Summary
                || PrivateStates.Any(item => item.Skipped || item.OptionId is not null)))
            || (Control.Status == SessionStatus.Completed && PublicState.Outcomes.Count != cards.Count))
        {
            throw new ArgumentException("Inconsistent session stage, status or private state.");
        }

        var outcomes = PublicState.Outcomes.Select((item, index) =>
        {
            var card = cards[index];
            if (card.CardId != item.CardId)
            {
                throw new ArgumentException("Snapshot card order mismatch.");
            }

            if (item.Choices is null)
            {
                return (CardOutcome)new CardOutcome.Passed(card);
            }

            if (item.Choices.Count != 2 || !item.Choices.Keys.Order().SequenceEqual(new[] { Control.First, Control.Second }.Order()))
            {
                throw new ArgumentException("Invalid revealed participants.");
            }

            return new CardOutcome.Revealed(card, item.Choices.ToImmutableDictionary(choice => new ParticipantId(choice.Key),
                choice => card.FindOption(choice.Value) ?? throw new ArgumentException("Unknown revealed option.")));
        }).ToImmutableList();
        var privateStates = PrivateStates.ToImmutableDictionary(item => new ParticipantId(item.Participant), item =>
        {
            if (item.Skipped && item.OptionId is not null)
            {
                throw new ArgumentException("Conflicting private answer.");
            }

            if (outcomes.Count == cards.Count && (item.Skipped || item.OptionId is not null))
            {
                throw new ArgumentException("Completed cards cannot retain private answers.");
            }

            CardAnswer? answer = item.Skipped ? new CardAnswer.Skipped() : item.OptionId is null ? null :
                new CardAnswer.Chosen(cards[outcomes.Count].FindOption(item.OptionId) ?? throw new ArgumentException("Unknown private option."));
            return new ConstellationPrivateState(answer);
        });
        var actions = Actions.ToImmutableDictionary(item => new ActionId(item.ActionId), item =>
        {
            if (!participants.Contains(new(item.Actor)) || item.ResultingVersion > Control.ResourceVersion
                || item.ResultingVersion != item.ExpectedVersion + 1
                || !descriptor.ActionTypes.Contains(new ActionType(item.Type)))
            {
                throw new ArgumentException("Invalid action receipt.");
            }

            return new RecordedAction(new(item.Actor), new(item.ActionId), new(item.Type), new(item.ExpectedVersion),
                new(item.ResultingVersion), ActionFingerprint.Restore(item.Sha256));
        });
        var state = new ConstellationState(new(PublicState.Stage, outcomes), privateStates, new(pinnedContent, participants));
        return new(descriptor.WithContent(pinnedContent.ContentVersion), participants,
            GameSchedule.Restore(Control.StartedAt, Control.Deadline), Control.Status, new(Control.ResourceVersion), state, actions);
    }
}

public sealed record ConstellationSnapshotPublic(ConstellationStage Stage, IReadOnlyList<ConstellationSnapshotOutcome> Outcomes);
public sealed record ConstellationSnapshotOutcome(string CardId, IReadOnlyDictionary<Guid, string>? Choices);
public sealed record ConstellationSnapshotPrivate(Guid Participant, bool Skipped, string? OptionId);
public sealed record ConstellationSnapshotControl(int RulesVersion, int ContentVersion, int SchemaVersion,
    Guid First, Guid Second, DateTimeOffset StartedAt, DateTimeOffset Deadline, SessionStatus Status, long ResourceVersion);
public sealed record ConstellationSnapshotAction(Guid Actor, Guid ActionId, string Type, long ExpectedVersion, long ResultingVersion, string Sha256);
