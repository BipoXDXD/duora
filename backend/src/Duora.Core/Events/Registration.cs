namespace Duora.Core.Events;

/// <summary>
/// Participação de uma conta num evento (glossário). Só o <see cref="Event"/> muda o estado, porque a vaga e a posição
/// na fila mudam juntas na mesma transação (spec 4.2).
/// </summary>
public sealed class Registration
{
    private Registration(Guid id, Guid userId, DateTimeOffset registeredAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A inscrição precisa de id.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A inscrição precisa de uma conta identificada.", nameof(userId));
        }

        Id = id;
        UserId = userId;
        RegisteredAt = registeredAt;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    public RegistrationState State { get; private set; }

    /// <summary>Posição na lista de espera; existe só no estado <c>Waitlisted</c> e nunca é reaproveitada.</summary>
    public long? WaitlistPosition { get; private set; }

    public DateTimeOffset RegisteredAt { get; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? CheckedInAt { get; private set; }

    public DateTimeOffset? WithdrawnAt { get; private set; }

    /// <summary>Ocupa uma das vagas da capacidade.</summary>
    public bool HoldsSeat => State is RegistrationState.Confirmed or RegistrationState.CheckedIn;

    /// <summary>Ainda participa do evento: tem vaga ou espera por uma.</summary>
    public bool IsActive => HoldsSeat || State == RegistrationState.Waitlisted;

    internal static Registration Confirmed(Guid id, Guid userId, DateTimeOffset now)
    {
        var registration = new Registration(id, userId, now);
        registration.Confirm(now);
        return registration;
    }

    internal static Registration Waitlisted(Guid id, Guid userId, DateTimeOffset now, long position)
    {
        return new Registration(id, userId, now)
        {
            State = RegistrationState.Waitlisted,
            WaitlistPosition = position,
        };
    }

    internal void Confirm(DateTimeOffset now)
    {
        State = RegistrationState.Confirmed;
        WaitlistPosition = null;
        ConfirmedAt = now;
    }

    internal void CheckIn(DateTimeOffset now)
    {
        State = RegistrationState.CheckedIn;
        CheckedInAt = now;
    }

    internal void Withdraw(DateTimeOffset now)
    {
        State = RegistrationState.Withdrawn;
        WaitlistPosition = null;
        WithdrawnAt = now;
    }

    internal void MarkNoShow() => State = RegistrationState.NoShow;

    internal void Complete() => State = RegistrationState.Completed;
}
