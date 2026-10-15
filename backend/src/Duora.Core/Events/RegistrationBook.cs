namespace Duora.Core.Events;

/// <summary>
/// Vagas e lista de espera de um evento. Esconde a regra de lotação: nunca mais inscrições com vaga do que a
/// capacidade, fila em ordem de chegada e promoção sempre da menor posição (T-28). Os dois contadores são estado
/// próprio, gravados em <c>events.confirmed_count</c> e <c>events.next_waitlist_position</c>, para o
/// <c>CHECK (confirmed_count BETWEEN 0 AND capacity)</c> do banco proteger a lotação mesmo diante de um bug aqui
/// (concurrency.md, C5). Toda mudança de vaga passa por este tipo, que mantém o contador junto.
/// </summary>
internal sealed class RegistrationBook(int capacity)
{
    private const long FirstWaitlistPosition = 1;

    private readonly List<Registration> _registrations = [];

    /// <summary>Todas as inscrições, em ordem de chegada.</summary>
    public IReadOnlyList<Registration> All => _registrations;

    /// <summary>Inscrições que ocupam vaga agora (<c>Confirmed</c> e <c>CheckedIn</c>).</summary>
    public int ConfirmedCount { get; private set; }

    /// <summary>Posição que a próxima pessoa sem vaga recebe; só cresce, então nenhuma posição é reaproveitada.</summary>
    public long NextWaitlistPosition { get; private set; } = FirstWaitlistPosition;

    public IReadOnlyList<Registration> Waitlist =>
        [.. _registrations
            .Where(registration => registration.State == RegistrationState.Waitlisted)
            .OrderBy(registration => registration.WaitlistPosition)];

    private bool HasFreeSeat => ConfirmedCount < capacity;

    public Registration? Find(Guid userId) =>
        _registrations.Find(registration => registration.UserId == userId);

    /// <summary>Confirma se há vaga; senão, põe no fim da fila com a próxima posição.</summary>
    public void Add(Guid registrationId, Guid userId, DateTimeOffset now)
    {
        if (HasFreeSeat)
        {
            _registrations.Add(Registration.Confirmed(registrationId, userId, now));
            ConfirmedCount++;
            return;
        }

        _registrations.Add(Registration.Waitlisted(registrationId, userId, now, NextWaitlistPosition));
        NextWaitlistPosition++;
    }

    /// <summary>Retira a inscrição ativa; se ela tinha vaga, a vaga fica livre.</summary>
    public void Withdraw(Registration registration, DateTimeOffset now)
    {
        var heldSeat = registration.HoldsSeat;
        registration.Withdraw(now);
        if (heldSeat)
        {
            ConfirmedCount--;
        }
    }

    /// <summary>Dá a vaga livre, se houver, à inscrição de menor posição na espera; devolve quem foi promovido.</summary>
    public Registration? PromoteNext(DateTimeOffset now)
    {
        if (!HasFreeSeat || Waitlist is not [var next, ..])
        {
            return null;
        }

        next.Confirm(now);
        ConfirmedCount++;
        return next;
    }

    /// <summary>Quem tinha vaga e não fez check-in fica ausente e deixa a vaga.</summary>
    public void MarkNoShows()
    {
        foreach (var registration in InState(RegistrationState.Confirmed))
        {
            registration.MarkNoShow();
            ConfirmedCount--;
        }
    }

    /// <summary>Quem fez check-in conclui a participação e deixa a vaga.</summary>
    public void CompleteCheckedIn()
    {
        foreach (var registration in InState(RegistrationState.CheckedIn))
        {
            registration.Complete();
            ConfirmedCount--;
        }
    }

    private List<Registration> InState(RegistrationState state) =>
        _registrations.FindAll(registration => registration.State == state);
}
