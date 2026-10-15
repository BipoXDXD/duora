using System.Collections.Immutable;

namespace Duora.Core.Experiences;

/// <summary>
/// Sessão de jogo de um par (a sala, ADR-0016): versões fixadas, prazo, estado da experiência e registro das ações
/// aplicadas. Imutável: cada transição do <see cref="ExperienceEngine{TContent,TState,TAction,TView}"/> devolve uma
/// nova instância com a versão seguinte. O estado da experiência e o registro de ações são internos de propósito:
/// o que sai para um participante é só a projeção.
/// </summary>
public sealed class GameSession<TState, TAction>
    where TState : class
    where TAction : class, IGameAction
{
    private GameSession(
        ExperienceVersion experience,
        ParticipantPair participants,
        GameSchedule schedule,
        SessionStatus status,
        StateVersion version,
        TState state,
        ImmutableDictionary<ActionId, RecordedAction<TAction>> actionLog)
    {
        Experience = experience;
        Participants = participants;
        Schedule = schedule;
        Status = status;
        Version = version;
        State = state;
        ActionLog = actionLog;
    }

    public ExperienceVersion Experience { get; }

    public ParticipantPair Participants { get; }

    public GameSchedule Schedule { get; }

    public SessionStatus Status { get; }

    public StateVersion Version { get; }

    /// <summary>Estado público, privado por participante e de controle da experiência. Nunca vai inteiro ao cliente.</summary>
    internal TState State { get; }

    /// <summary>Ações já aplicadas, por id, para que um reenvio devolva o resultado original (INV-06).</summary>
    internal ImmutableDictionary<ActionId, RecordedAction<TAction>> ActionLog { get; }

    internal static GameSession<TState, TAction> Start(
        ExperienceVersion experience, ParticipantPair participants, GameSchedule schedule, TState state) =>
        new(experience, participants, schedule, SessionStatus.Active, StateVersion.Initial, state,
            ImmutableDictionary<ActionId, RecordedAction<TAction>>.Empty);

    internal GameSession<TState, TAction> AfterAction(
        TState state, SessionStatus status, ActionId actionId, RecordedAction<TAction> recorded) =>
        new(Experience, Participants, Schedule, status, recorded.ResultingVersion, state,
            ActionLog.Add(actionId, recorded));

    internal GameSession<TState, TAction> AfterDeadline(TState state, SessionStatus status) =>
        new(Experience, Participants, Schedule, status, Version.Next(), state, ActionLog);
}

/// <summary>
/// Ação aplicada, guardada com o corpo inteiro do comando (autor, versão esperada e ação) para distinguir reenvio
/// de reuso do id com outro corpo (spec 8.7).
/// </summary>
internal sealed record RecordedAction<TAction>(
    ParticipantId Actor, StateVersion ExpectedVersion, TAction Action, StateVersion ResultingVersion)
    where TAction : class, IGameAction
{
    public bool IsSameAs(GameCommand<TAction> command) =>
        Actor == command.Actor && ExpectedVersion == command.ExpectedVersion && Action.Equals(command.Action);
}
