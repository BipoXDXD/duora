namespace Duora.Core.Experiences;

/// <summary>Fato ocorrido numa sessão de jogo. Só ids e versões, nenhum conteúdo privado de participante.</summary>
public abstract record GameEvent(DateTimeOffset OccurredAt);

public sealed record ActionApplied(
    ActionId ActionId, ParticipantId Actor, ActionType Type, StateVersion Version, DateTimeOffset OccurredAt)
    : GameEvent(OccurredAt);

public sealed record SessionFinished(SessionStatus Outcome, StateVersion Version, DateTimeOffset OccurredAt)
    : GameEvent(OccurredAt);
