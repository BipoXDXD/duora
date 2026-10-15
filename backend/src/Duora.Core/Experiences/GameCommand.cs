namespace Duora.Core.Experiences;

/// <summary>Comando de jogo de um participante (spec 8.6), já convertido para tipos de domínio na fronteira.</summary>
public sealed record GameCommand<TAction>
    where TAction : class, IGameAction
{
    private readonly TAction _action;

    public GameCommand(ActionId actionId, ParticipantId actor, StateVersion expectedVersion, TAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        ActionId = actionId;
        Actor = actor;
        ExpectedVersion = expectedVersion;
        _action = action;
    }

    public ActionId ActionId { get; init; }

    public ParticipantId Actor { get; init; }

    public StateVersion ExpectedVersion { get; init; }

    public TAction Action
    {
        get => _action;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _action = value;
        }
    }
}
