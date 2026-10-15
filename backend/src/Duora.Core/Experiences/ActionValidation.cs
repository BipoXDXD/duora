namespace Duora.Core.Experiences;

/// <summary>Resultado de <c>ValidateAction</c>. Conjunto fechado: os construtores dos casos são privados à hierarquia.</summary>
public abstract record ActionValidation<TState, TAction>
    where TState : class
    where TAction : class, IGameAction
{
    private ActionValidation()
    {
    }

    /// <summary>A ação pode ser aplicada sobre o estado em que foi validada.</summary>
    public sealed record Accepted(ValidatedAction<TState, TAction> Action) : ActionValidation<TState, TAction>;

    /// <summary>O mesmo <see cref="ActionId"/> já foi aplicado: devolve o resultado original, sem novo efeito.</summary>
    public sealed record AlreadyApplied(ActionReceipt Receipt) : ActionValidation<TState, TAction>;

    public sealed record Rejected(RejectionReason Reason) : ActionValidation<TState, TAction>;

    /// <summary>O <c>expectedVersion</c> difere da versão atual; o cliente recarrega o estado (spec 8.6, 10.4).</summary>
    public sealed record Conflict(StateVersion CurrentVersion) : ActionValidation<TState, TAction>;
}

/// <summary>
/// Ação aceita por <c>ValidateAction</c>, presa ao estado em que foi validada. Só o motor a constrói, então
/// <c>ApplyAction</c> nunca recebe ação sem validação.
/// </summary>
public sealed class ValidatedAction<TState, TAction>
    where TState : class
    where TAction : class, IGameAction
{
    internal ValidatedAction(GameSession<TState, TAction> session, GameCommand<TAction> command)
    {
        Session = session;
        Command = command;
    }

    public GameCommand<TAction> Command { get; }

    internal GameSession<TState, TAction> Session { get; }
}

/// <summary>Resultado de <c>ApplyAction</c>: nova sessão, recibo para o cliente e eventos para a outbox.</summary>
public sealed record AppliedAction<TState, TAction>(
    GameSession<TState, TAction> Session, ActionReceipt Receipt, IReadOnlyList<GameEvent> Events)
    where TState : class
    where TAction : class, IGameAction;
