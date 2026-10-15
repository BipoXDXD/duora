using System.Diagnostics.CodeAnalysis;

namespace Duora.Core.Events;

/// <summary>
/// Evento e coordenador da sua lotação: estado, capacidade, inscrições e lista de espera formam um único agregado,
/// porque a persistência tranca a linha do evento para inscrever, retirar e promover na mesma transação
/// (spec 10.4, cenário "duas inscrições na última vaga"). Com até 100 vagas (spec 4.3) e a fila, carregar as
/// inscrições inteiras é barato. As rodadas são agregados próprios (<see cref="Round"/>), ligados pelo id do evento.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "O glossário (docs/product/scope.md) fixa Event como nome no código; o conflito é com a palavra-chave Event do VB, e nenhum consumidor em VB usa o Duora.Core.")]
public sealed class Event
{
    private readonly RegistrationBook _registrations;

    private Event(Guid id, EventSchedule schedule, int capacity, ExperienceVersionRef experience)
    {
        Id = id;
        Schedule = schedule;
        Capacity = capacity;
        Experience = experience;
        State = EventState.Draft;
        _registrations = new RegistrationBook(capacity);
    }

    public Guid Id { get; }

    public EventSchedule Schedule { get; }

    public int Capacity { get; }

    public ExperienceVersionRef Experience { get; }

    public EventState State { get; private set; }

    public EventCancellation? Cancellation { get; private set; }

    /// <summary>Inscrições com vaga (<c>Confirmed</c> e <c>CheckedIn</c>); nunca passa de <see cref="Capacity"/>.</summary>
    public int ConfirmedCount => _registrations.SeatsTaken;

    /// <summary>Todas as inscrições, em ordem de chegada.</summary>
    public IReadOnlyList<Registration> Registrations => _registrations.All;

    /// <summary>Inscrições em espera, da menor para a maior posição.</summary>
    public IReadOnlyList<Registration> Waitlist => _registrations.Waitlist;

    /// <summary>
    /// Cria o rascunho com a configuração já validada: capacidade entre 1 e o máximo do snapshot e check-in ainda no
    /// futuro. A versão de experiência é conferida no catálogo só na publicação.
    /// </summary>
    public static EventDraftCreation CreateDraft(
        Guid id,
        EventSchedule schedule,
        int capacity,
        ExperienceVersionRef experience,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("O evento precisa de id.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(experience);

        if (capacity < 1 || capacity > schedule.Parameters.MaxParticipants)
        {
            return EventDraftCreation.Refused(EventDraftRefusal.CapacityOutOfRange);
        }

        if (now >= schedule.CheckInOpensAt)
        {
            return EventDraftCreation.Refused(EventDraftRefusal.ScheduleNotInFuture);
        }

        return EventDraftCreation.Created(new Event(id, schedule, capacity, experience));
    }

    public Registration? RegistrationOf(Guid userId) => _registrations.Find(userId);

    /// <summary>Publica o rascunho se a versão de experiência está publicada e o check-in ainda não abriu.</summary>
    public Outcome<EventTransitionRefusal> Publish(ExperienceVersionStatus experienceStatus, DateTimeOffset now)
    {
        if (State != EventState.Draft)
        {
            return Outcome<EventTransitionRefusal>.Refused(EventTransitionRefusal.InvalidState);
        }

        if (experienceStatus != ExperienceVersionStatus.Published)
        {
            return Outcome<EventTransitionRefusal>.Refused(EventTransitionRefusal.ExperienceVersionNotPublished);
        }

        if (now >= Schedule.CheckInOpensAt)
        {
            return Outcome<EventTransitionRefusal>.Refused(EventTransitionRefusal.ScheduleNotInFuture);
        }

        State = EventState.Published;
        return Outcome<EventTransitionRefusal>.Accepted();
    }

    public Outcome<EventTransitionRefusal> OpenCheckIn(DateTimeOffset now) =>
        AdvanceWhenDue(EventState.Published, EventState.CheckInOpen, Schedule.CheckInOpensAt, now);

    /// <summary>Inicia o evento; quem tinha vaga e não fez check-in vira <c>NoShow</c>.</summary>
    public Outcome<EventTransitionRefusal> Start(DateTimeOffset now)
    {
        var outcome = AdvanceWhenDue(EventState.CheckInOpen, EventState.Running, Schedule.StartsAt, now);
        if (outcome.IsAccepted)
        {
            MarkRegistrations(RegistrationState.Confirmed, registration => registration.MarkNoShow());
        }

        return outcome;
    }

    /// <summary>Conclui o evento depois da última rodada; quem fez check-in fica <c>Completed</c>.</summary>
    public Outcome<EventTransitionRefusal> Complete(DateTimeOffset now)
    {
        var outcome = AdvanceWhenDue(EventState.Running, EventState.Completed, Schedule.EndsAt, now);
        if (outcome.IsAccepted)
        {
            MarkRegistrations(RegistrationState.CheckedIn, registration => registration.Complete());
        }

        return outcome;
    }

    /// <summary>
    /// Cancela o evento antes do fim. Devolve o registro de auditoria e um aviso para cada inscrição ativa; rodadas e
    /// reservas são canceladas pelos próprios agregados na mesma transação (API-ADM05).
    /// </summary>
    public Outcome<EventTransitionRefusal> Cancel(Guid cancelledBy, CancellationReason reason, DateTimeOffset now)
    {
        if (cancelledBy == Guid.Empty)
        {
            throw new ArgumentException("O cancelamento precisa de um autor identificado.", nameof(cancelledBy));
        }

        ArgumentNullException.ThrowIfNull(reason);

        if (State is EventState.Completed or EventState.Cancelled)
        {
            return Outcome<EventTransitionRefusal>.Refused(EventTransitionRefusal.InvalidState);
        }

        State = EventState.Cancelled;
        Cancellation = new EventCancellation(cancelledBy, reason, now);

        List<Effect> effects = [new EventCancelled(Id, cancelledBy, reason, now)];
        effects.AddRange(_registrations.All
            .Where(registration => registration.IsActive)
            .Select(registration => new CancellationNoticeRequested(Id, registration.UserId, now)));
        return Outcome<EventTransitionRefusal>.Accepted(effects);
    }

    /// <summary>Inscreve a pessoa: vaga confirmada se houver, senão a próxima posição da espera (API-E03).</summary>
    public Outcome<RegistrationRefusal> Register(
        Guid registrationId,
        Guid userId,
        ParticipantEligibility eligibility,
        DateTimeOffset now)
    {
        if (!AcceptsRegistrations(now))
        {
            return Outcome<RegistrationRefusal>.Refused(RegistrationRefusal.EventNotOpenForRegistration);
        }

        if (eligibility != ParticipantEligibility.Eligible)
        {
            return Outcome<RegistrationRefusal>.Refused(RegistrationRefusal.ParticipantIneligible);
        }

        if (_registrations.Find(userId) is not null)
        {
            return Outcome<RegistrationRefusal>.Refused(RegistrationRefusal.AlreadyRegistered);
        }

        _registrations.Add(registrationId, userId, now);
        return Outcome<RegistrationRefusal>.Accepted();
    }

    /// <summary>
    /// Retira a inscrição (API-E04). Uma vaga liberada antes do início vai para a menor posição da espera, devolvida
    /// como efeito para virar notificação.
    /// </summary>
    public Outcome<WithdrawalRefusal> Withdraw(Guid userId, DateTimeOffset now)
    {
        if (State is EventState.Completed or EventState.Cancelled)
        {
            return Outcome<WithdrawalRefusal>.Refused(WithdrawalRefusal.EventClosed);
        }

        var registration = _registrations.Find(userId);
        if (registration is null)
        {
            return Outcome<WithdrawalRefusal>.Refused(WithdrawalRefusal.NotRegistered);
        }

        if (!registration.IsActive)
        {
            return Outcome<WithdrawalRefusal>.Refused(WithdrawalRefusal.RegistrationNotActive);
        }

        registration.Withdraw(now);
        return Outcome<WithdrawalRefusal>.Accepted(PromoteFromWaitlist(now));
    }

    /// <summary>
    /// Confirma a presença de quem tem vaga, dentro da janela [abertura do check-in, início). O horário decide, não o
    /// estado: o job que move o evento para <c>CheckInOpen</c> pode atrasar (API-E05). Repetir é aceito sem efeito.
    /// </summary>
    public Outcome<CheckInRefusal> CheckIn(Guid userId, ParticipantEligibility eligibility, DateTimeOffset now)
    {
        if (State is not (EventState.Published or EventState.CheckInOpen))
        {
            return Outcome<CheckInRefusal>.Refused(CheckInRefusal.EventNotAcceptingCheckIn);
        }

        if (now < Schedule.CheckInOpensAt)
        {
            return Outcome<CheckInRefusal>.Refused(CheckInRefusal.CheckInNotOpenYet);
        }

        if (now >= Schedule.StartsAt)
        {
            return Outcome<CheckInRefusal>.Refused(CheckInRefusal.CheckInClosed);
        }

        return CheckInRegistration(userId, eligibility, now);
    }

    private Outcome<CheckInRefusal> CheckInRegistration(
        Guid userId,
        ParticipantEligibility eligibility,
        DateTimeOffset now)
    {
        var registration = _registrations.Find(userId);
        if (registration is null)
        {
            return Outcome<CheckInRefusal>.Refused(CheckInRefusal.NotRegistered);
        }

        if (registration.State == RegistrationState.CheckedIn)
        {
            return Outcome<CheckInRefusal>.Accepted();
        }

        if (registration.State != RegistrationState.Confirmed)
        {
            return Outcome<CheckInRefusal>.Refused(CheckInRefusal.RegistrationNotConfirmed);
        }

        if (eligibility != ParticipantEligibility.Eligible)
        {
            return Outcome<CheckInRefusal>.Refused(CheckInRefusal.ParticipantIneligible);
        }

        registration.CheckIn(now);
        return Outcome<CheckInRefusal>.Accepted();
    }

    /// <summary>Inscrição e promoção só fazem sentido enquanto quem entra ainda pode fazer check-in.</summary>
    private bool AcceptsRegistrations(DateTimeOffset now) =>
        State is EventState.Published or EventState.CheckInOpen && now < Schedule.StartsAt;

    private IReadOnlyList<Effect> PromoteFromWaitlist(DateTimeOffset now)
    {
        if (!AcceptsRegistrations(now))
        {
            return [];
        }

        var promoted = _registrations.PromoteNext(now);
        return promoted is null ? [] : [new WaitlistedRegistrationPromoted(Id, promoted.UserId, now)];
    }

    private Outcome<EventTransitionRefusal> AdvanceWhenDue(
        EventState from,
        EventState to,
        DateTimeOffset dueAt,
        DateTimeOffset now)
    {
        if (State != from)
        {
            return Outcome<EventTransitionRefusal>.Refused(EventTransitionRefusal.InvalidState);
        }

        if (now < dueAt)
        {
            return Outcome<EventTransitionRefusal>.Refused(EventTransitionRefusal.NotDueYet);
        }

        State = to;
        return Outcome<EventTransitionRefusal>.Accepted();
    }

    private void MarkRegistrations(RegistrationState current, Action<Registration> transition)
    {
        foreach (var registration in _registrations.All.Where(registration => registration.State == current))
        {
            transition(registration);
        }
    }
}
