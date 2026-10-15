namespace Duora.Core.Experiences;

/// <summary>Resultado de <c>HandleDeadline</c>. Conjunto fechado, sem exceção para os casos normais.</summary>
public abstract record DeadlineOutcome<TState, TAction>
    where TState : class
    where TAction : class, IGameAction
{
    private DeadlineOutcome()
    {
    }

    /// <summary>A sessão foi concluída ou expirou; a versão subiu uma unidade.</summary>
    public sealed record Resolved(GameSession<TState, TAction> Session, IReadOnlyList<GameEvent> Events)
        : DeadlineOutcome<TState, TAction>;

    public sealed record NotDue(DateTimeOffset Deadline) : DeadlineOutcome<TState, TAction>;

    /// <summary>A sessão já terminou antes; nada a fazer.</summary>
    public sealed record AlreadyFinished(SessionStatus Status) : DeadlineOutcome<TState, TAction>;

    /// <summary>Uma ação venceu a corrida; o job relê a sessão (spec 10.4, "timeout vs ação").</summary>
    public sealed record VersionConflict(StateVersion CurrentVersion) : DeadlineOutcome<TState, TAction>;
}
