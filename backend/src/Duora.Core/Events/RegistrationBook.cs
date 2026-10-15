namespace Duora.Core.Events;

/// <summary>
/// Vagas e lista de espera de um evento. Esconde a regra de lotação: nunca mais inscrições com vaga do que a
/// capacidade, fila em ordem de chegada e promoção sempre da menor posição (T-28).
/// </summary>
internal sealed class RegistrationBook(int capacity)
{
    private readonly List<Registration> _registrations = [];
    private long _nextWaitlistPosition = 1;

    /// <summary>Todas as inscrições, em ordem de chegada.</summary>
    public IReadOnlyList<Registration> All => _registrations;

    public int SeatsTaken => _registrations.Count(registration => registration.HoldsSeat);

    public IReadOnlyList<Registration> Waitlist =>
        [.. _registrations
            .Where(registration => registration.State == RegistrationState.Waitlisted)
            .OrderBy(registration => registration.WaitlistPosition)];

    public Registration? Find(Guid userId) =>
        _registrations.Find(registration => registration.UserId == userId);

    /// <summary>Confirma se há vaga; senão, põe no fim da fila com a próxima posição.</summary>
    public void Add(Guid registrationId, Guid userId, DateTimeOffset now)
    {
        var registration = HasFreeSeat
            ? Registration.Confirmed(registrationId, userId, now)
            : Registration.Waitlisted(registrationId, userId, now, _nextWaitlistPosition++);
        _registrations.Add(registration);
    }

    /// <summary>Dá a vaga livre, se houver, à inscrição de menor posição na espera; devolve quem foi promovido.</summary>
    public Registration? PromoteNext(DateTimeOffset now)
    {
        if (!HasFreeSeat)
        {
            return null;
        }

        if (Waitlist is not [var next, ..])
        {
            return null;
        }

        next.Confirm(now);
        return next;
    }

    private bool HasFreeSeat => SeatsTaken < capacity;
}
