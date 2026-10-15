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
        ImmutableDictionary<ActionId, RecordedAction> actionLog)
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
    internal ImmutableDictionary<ActionId, RecordedAction> ActionLog { get; }

    internal static GameSession<TState, TAction> Start(
        ExperienceVersion experience, ParticipantPair participants, GameSchedule schedule, TState state) =>
        new(experience, participants, schedule, SessionStatus.Active, StateVersion.Initial, state,
            ImmutableDictionary<ActionId, RecordedAction>.Empty);

    internal GameSession<TState, TAction> AfterAction(TState state, SessionStatus status, RecordedAction recorded) =>
        new(Experience, Participants, Schedule, status, recorded.ResultingVersion, state,
            ActionLog.Add(recorded.ActionId, recorded));

    internal GameSession<TState, TAction> AfterDeadline(TState state, SessionStatus status) =>
        new(Experience, Participants, Schedule, status, Version.Next(), state, ActionLog);
}

/// <summary>
/// Resultado mínimo de uma ação aplicada (spec 8.7): autor, id, tipo, versões e o hash canônico do corpo. O corpo
/// não fica guardado, porque pode ser uma escolha privada (INV-05); o hash basta para distinguir reenvio de reuso
/// do id com outro corpo, inclusive depois de uma reidratação (ADR-0020).
/// </summary>
internal sealed record RecordedAction(
    ParticipantId Actor,
    ActionId ActionId,
    ActionType Type,
    StateVersion ExpectedVersion,
    StateVersion ResultingVersion,
    ActionFingerprint Fingerprint)
{
    public static RecordedAction Of<TAction>(GameCommand<TAction> command, StateVersion resultingVersion)
        where TAction : class, IGameAction =>
        new(command.Actor, command.ActionId, command.Action.Type, command.ExpectedVersion, resultingVersion,
            ActionFingerprint.Of(command.Action));

    public bool IsSameAs<TAction>(GameCommand<TAction> command)
        where TAction : class, IGameAction =>
        Actor == command.Actor
        && Type == command.Action.Type
        && ExpectedVersion == command.ExpectedVersion
        && Fingerprint == ActionFingerprint.Of(command.Action);
}
