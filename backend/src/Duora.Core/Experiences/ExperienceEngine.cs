namespace Duora.Core.Experiences;

/// <summary>
/// Contrato do motor de experiências (spec 6.1). Puro: o horário vem do <see cref="TimeProvider"/> injetado e a
/// aleatoriedade, da semente. Concorrência entre ações e prazo segue a mesma regra: toda transição exige a versão
/// atual, e a persistência grava com atualização condicionada a ela (spec 10.4).
/// </summary>
public sealed class ExperienceEngine<TContent, TState, TAction, TView>
    where TContent : IExperienceContent
    where TState : class
    where TAction : class, IGameAction
    where TView : class
{
    private readonly IExperienceRules<TContent, TState, TAction, TView> _rules;
    private readonly TimeProvider _clock;

    public ExperienceEngine(IExperienceRules<TContent, TState, TAction, TView> rules, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(clock);

        _rules = rules;
        _clock = clock;
    }

    /// <summary>Inicia a sessão agora, com o conteúdo publicado recebido. Não consulta LLM nem catálogo (ADR-0013).</summary>
    public GameSession<TState, TAction> CreateSession(
        TContent content,
        ParticipantPair participants,
        DateTimeOffset roundStartedAt,
        GameTimeLimits limits,
        SessionSeed seed)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(participants);
        if (content.ExperienceId != _rules.Descriptor.ExperienceId)
        {
            throw new ArgumentException(
                $"Conteúdo de '{content.ExperienceId}' não serve às regras de '{_rules.Descriptor.ExperienceId}'.",
                nameof(content));
        }

        var schedule = GameSchedule.Plan(roundStartedAt, _clock.GetUtcNow(), limits);
        var state = _rules.CreateInitialState(content, participants, seed);
        return GameSession<TState, TAction>.Start(
            _rules.Descriptor.WithContent(content.ContentVersion), participants, schedule, state);
    }

    /// <summary>
    /// Ordem das checagens: participação (antes de tudo, para não confirmar nada a terceiros), reenvio, sessão
    /// ativa, prazo, versão esperada e, por fim, as regras da etapa.
    /// </summary>
    public ActionValidation<TState, TAction> ValidateAction(
        GameSession<TState, TAction> session, GameCommand<TAction> command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);

        if (!session.Participants.Contains(command.Actor))
        {
            return new ActionValidation<TState, TAction>.Rejected(RejectionReason.NotParticipant);
        }

        if (session.ActionLog.TryGetValue(command.ActionId, out var recorded))
        {
            return recorded.IsSameAs(command)
                ? new ActionValidation<TState, TAction>.AlreadyApplied(
                    new ActionReceipt(command.ActionId, recorded.ResultingVersion))
                : new ActionValidation<TState, TAction>.Rejected(RejectionReason.ActionIdReused);
        }

        if (session.Status != SessionStatus.Active)
        {
            return new ActionValidation<TState, TAction>.Rejected(RejectionReason.SessionFinished);
        }

        if (session.Schedule.IsDue(_clock.GetUtcNow()))
        {
            return new ActionValidation<TState, TAction>.Rejected(RejectionReason.DeadlineReached);
        }

        if (command.ExpectedVersion != session.Version)
        {
            return new ActionValidation<TState, TAction>.Conflict(session.Version);
        }

        return _rules.Validate(session.State, command.Actor, command.Action) is { } reason
            ? new ActionValidation<TState, TAction>.Rejected(reason)
            : new ActionValidation<TState, TAction>.Accepted(new ValidatedAction<TState, TAction>(session, command));
    }

    public AppliedAction<TState, TAction> ApplyAction(ValidatedAction<TState, TAction> validated)
    {
        ArgumentNullException.ThrowIfNull(validated);

        var (session, command) = (validated.Session, validated.Command);
        var transition = _rules.Apply(session.State, command.Actor, command.Action);
        var version = session.Version.Next();
        var status = transition.IsConcluded ? SessionStatus.Completed : SessionStatus.Active;
        var next = session.AfterAction(transition.NextState, status, RecordedAction.Of(command, version));

        var now = _clock.GetUtcNow();
        List<GameEvent> events = [new ActionApplied(command.ActionId, command.Actor, command.Action.Type, version, now)];
        if (transition.IsConcluded)
        {
            events.Add(new SessionFinished(SessionStatus.Completed, version, now));
        }

        return new AppliedAction<TState, TAction>(next, new ActionReceipt(command.ActionId, version), events);
    }

    /// <summary>Projeção para um participante; <c>null</c> para quem não é do par (a API responde 404).</summary>
    public TView? ProjectForParticipant(GameSession<TState, TAction> session, ParticipantId viewer)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.Participants.Contains(viewer) ? _rules.Project(session, viewer) : null;
    }

    public DeadlineOutcome<TState, TAction> HandleDeadline(
        GameSession<TState, TAction> session, StateVersion expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Status != SessionStatus.Active)
        {
            return new DeadlineOutcome<TState, TAction>.AlreadyFinished(session.Status);
        }

        if (expectedVersion != session.Version)
        {
            return new DeadlineOutcome<TState, TAction>.VersionConflict(session.Version);
        }

        var now = _clock.GetUtcNow();
        if (!session.Schedule.IsDue(now))
        {
            return new DeadlineOutcome<TState, TAction>.NotDue(session.Schedule.Deadline);
        }

        var transition = _rules.ResolveDeadline(session.State);
        var status = transition.IsConcluded ? SessionStatus.Completed : SessionStatus.Expired;
        var next = session.AfterDeadline(transition.NextState, status);
        return new DeadlineOutcome<TState, TAction>.Resolved(next, [new SessionFinished(status, next.Version, now)]);
    }

    public AccessibleInteraction DescribeAccessibleInteraction() => _rules.DescribeAccessibleInteraction();
}
