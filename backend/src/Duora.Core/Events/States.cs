namespace Duora.Core.Events;

/// <summary>Estados do evento (spec 4.2).</summary>
public enum EventState
{
    Draft = 1,
    Published,
    CheckInOpen,
    Running,
    Completed,
    Cancelled,
}

/// <summary>Estados da inscrição (spec 4.2).</summary>
public enum RegistrationState
{
    Waitlisted = 1,
    Confirmed,
    CheckedIn,
    Withdrawn,
    NoShow,
    Completed,
}

/// <summary>Estados da rodada (spec 4.2).</summary>
public enum RoundState
{
    Scheduled = 1,
    Preparing,
    Inviting,
    Running,
    Closed,
    Cancelled,
}

/// <summary>Estado editorial da versão de experiência, como o catálogo informa na publicação do evento.</summary>
public enum ExperienceVersionStatus
{
    Draft = 1,
    Published,
    Retired,
}

/// <summary>Elegibilidade atual da conta (estado, requisito etário e sanções), como Identity e Safety informam.</summary>
public enum ParticipantEligibility
{
    Eligible = 1,
    Ineligible,
}
