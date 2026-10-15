namespace Duora.Core.Events;

/// <summary>
/// Rodada de um evento. Cada transição é condicionada à versão lida pelo worker: a mesma chamada repetida com a mesma
/// versão é aceita sem efeito, e uma versão diferente é conflito (INV-06, INV-08). Os horários vêm do snapshot de
/// parâmetros do evento na criação e não mudam depois.
/// </summary>
public sealed class Round
{
    private const long InitialVersion = 1;

    private Round(Guid id, Guid eventId, int number, DateTimeOffset startsAt, EventParameters parameters)
    {
        Id = id;
        EventId = eventId;
        Number = number;
        StartsAt = startsAt;
        StartDeadline = startsAt + parameters.RoundStartTolerance;
        EndsAt = startsAt + parameters.RoundWindow;
        State = RoundState.Scheduled;
        Version = InitialVersion;
    }

    public Guid Id { get; }

    public Guid EventId { get; }

    /// <summary>Número da rodada no evento, a partir de 1.</summary>
    public int Number { get; }

    public DateTimeOffset StartsAt { get; }

    /// <summary>Limite para a rodada começar a rodar; depois dele não cabe mais um jogo na janela.</summary>
    public DateTimeOffset StartDeadline { get; }

    /// <summary>Fim da janela da rodada (<c>deadline_at</c>).</summary>
    public DateTimeOffset EndsAt { get; }

    public RoundState State { get; private set; }

    public long Version { get; private set; }

    private bool IsFinished => State is RoundState.Closed or RoundState.Cancelled;

    public static Round Schedule(Guid id, Guid eventId, int number, EventSchedule schedule)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A rodada precisa de id.", nameof(id));
        }

        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("A rodada precisa do id do evento.", nameof(eventId));
        }

        ArgumentNullException.ThrowIfNull(schedule);

        return new Round(id, eventId, number, schedule.RoundStartsAt(number), schedule.Parameters);
    }

    /// <summary>Começa a montar os pares, a partir do horário da rodada e antes do limite de início.</summary>
    public Outcome<RoundRefusal> BeginPreparation(long expectedVersion, DateTimeOffset now) =>
        Advance(expectedVersion, RoundState.Scheduled, RoundState.Preparing, StartTimingRefusal(now), []);

    /// <summary>Os pares foram formados e os convites saem, ainda antes do limite de início.</summary>
    public Outcome<RoundRefusal> BeginInviting(long expectedVersion, DateTimeOffset now) =>
        Advance(expectedVersion, RoundState.Preparing, RoundState.Inviting, StartTimingRefusal(now), []);

    /// <summary>
    /// As salas aceitas entram em jogo, ainda antes do limite de início: depois dele o jogo não cabe mais até o início
    /// + 9 min (spec 4.3). É a mesma regra que <see cref="Resume"/> aplica depois de um reinício.
    /// </summary>
    public Outcome<RoundRefusal> BeginRunning(long expectedVersion, DateTimeOffset now) =>
        Advance(expectedVersion, RoundState.Inviting, RoundState.Running, StartTimingRefusal(now), []);

    /// <summary>Encerra a rodada no fim da janela e pede a liberação das reservas que sobraram.</summary>
    public Outcome<RoundRefusal> Close(long expectedVersion, DateTimeOffset now)
    {
        RoundRefusal? timing = now < EndsAt ? RoundRefusal.NotDueYet : null;
        return Advance(
            expectedVersion,
            RoundState.Running,
            RoundState.Closed,
            timing,
            [new ReservationsReleaseRequested(EventId, Id, now)]);
    }

    /// <summary>Cancela a rodada não encerrada, liberando reservas e avisando os participantes de forma neutra.</summary>
    public Outcome<RoundRefusal> Cancel(long expectedVersion, DateTimeOffset now)
    {
        if (WasAlreadyApplied(expectedVersion, RoundState.Cancelled))
        {
            return Outcome<RoundRefusal>.Accepted();
        }

        if (Version != expectedVersion)
        {
            return Outcome<RoundRefusal>.Refused(RoundRefusal.VersionConflict);
        }

        if (IsFinished)
        {
            return Outcome<RoundRefusal>.Refused(RoundRefusal.InvalidState);
        }

        return FinishAbruptly(RoundState.Cancelled, now);
    }

    /// <summary>
    /// Política de retomada após reinício (spec 5.2). Dentro da tolerância, mantém o estado persistido; fora dela,
    /// cancela a rodada que ainda não rodava ou encerra a que rodava, liberando reservas e avisando de forma neutra.
    /// Uma rodada encerrada ou cancelada nunca recomeça (INV-08).
    /// </summary>
    public Outcome<RoundRefusal> Resume(long expectedVersion, DateTimeOffset now)
    {
        if (IsFinished)
        {
            return Outcome<RoundRefusal>.Accepted();
        }

        if (Version != expectedVersion)
        {
            return Outcome<RoundRefusal>.Refused(RoundRefusal.VersionConflict);
        }

        if (State == RoundState.Running)
        {
            return now < EndsAt ? Outcome<RoundRefusal>.Accepted() : FinishAbruptly(RoundState.Closed, now);
        }

        return now < StartDeadline ? Outcome<RoundRefusal>.Accepted() : FinishAbruptly(RoundState.Cancelled, now);
    }

    private RoundRefusal? StartTimingRefusal(DateTimeOffset now)
    {
        if (now < StartsAt)
        {
            return RoundRefusal.NotDueYet;
        }

        return now >= StartDeadline ? RoundRefusal.StartWindowElapsed : null;
    }

    private Outcome<RoundRefusal> Advance(
        long expectedVersion,
        RoundState from,
        RoundState to,
        RoundRefusal? timingRefusal,
        IReadOnlyList<Effect> effects)
    {
        if (WasAlreadyApplied(expectedVersion, to))
        {
            return Outcome<RoundRefusal>.Accepted();
        }

        if (Version != expectedVersion)
        {
            return Outcome<RoundRefusal>.Refused(RoundRefusal.VersionConflict);
        }

        if (State != from)
        {
            return Outcome<RoundRefusal>.Refused(RoundRefusal.InvalidState);
        }

        if (timingRefusal is { } refusal)
        {
            return Outcome<RoundRefusal>.Refused(refusal);
        }

        MoveTo(to);
        return Outcome<RoundRefusal>.Accepted(effects);
    }

    /// <summary>
    /// Cada transição soma exatamente 1 à versão; estar no destino com a versão seguinte à esperada significa que esta
    /// mesma transição já foi aplicada a partir daquela versão.
    /// </summary>
    private bool WasAlreadyApplied(long expectedVersion, RoundState target) =>
        State == target && Version == expectedVersion + 1;

    private Outcome<RoundRefusal> FinishAbruptly(RoundState finalState, DateTimeOffset now)
    {
        MoveTo(finalState);
        return Outcome<RoundRefusal>.Accepted(
        [
            new ReservationsReleaseRequested(EventId, Id, now),
            new UnavailabilityNoticeRequested(EventId, Id, now),
        ]);
    }

    private void MoveTo(RoundState state)
    {
        State = state;
        Version++;
    }
}
