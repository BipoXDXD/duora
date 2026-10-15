namespace Duora.Core.Events;

/// <summary>Por que um rascunho de evento não foi criado.</summary>
public enum EventDraftRefusal
{
    /// <summary>Capacidade fora de 1 até o máximo de participantes do snapshot.</summary>
    CapacityOutOfRange = 1,

    /// <summary>O check-in já abriu (ou abre agora): não sobra tempo de inscrição.</summary>
    ScheduleNotInFuture,
}

/// <summary>Por que uma transição do ciclo de vida do evento foi recusada.</summary>
public enum EventTransitionRefusal
{
    /// <summary>A transição não sai do estado atual.</summary>
    InvalidState = 1,

    /// <summary>O horário que libera a transição ainda não chegou.</summary>
    NotDueYet,

    /// <summary>O check-in já abriu; o evento não pode mais ser publicado com esse horário.</summary>
    ScheduleNotInFuture,

    /// <summary>A versão de experiência referenciada não está publicada no catálogo.</summary>
    ExperienceVersionNotPublished,
}

/// <summary>Por que uma inscrição foi recusada (API-E03).</summary>
public enum RegistrationRefusal
{
    /// <summary>Evento fora de <c>Published</c>/<c>CheckInOpen</c> ou já no horário de início.</summary>
    EventNotOpenForRegistration = 1,

    /// <summary>A pessoa já tem a sua única inscrição neste evento, ativa ou não.</summary>
    AlreadyRegistered,

    /// <summary>A conta não está elegível agora (INV-01).</summary>
    ParticipantIneligible,
}

/// <summary>Por que uma retirada foi recusada (API-E04).</summary>
public enum WithdrawalRefusal
{
    /// <summary>O evento terminou ou foi cancelado.</summary>
    EventClosed = 1,

    NotRegistered,

    /// <summary>A inscrição já saiu da lista ativa (retirada, ausência ou concluída).</summary>
    RegistrationNotActive,
}

/// <summary>Por que um check-in foi recusado (API-E05).</summary>
public enum CheckInRefusal
{
    /// <summary>Evento em rascunho, iniciado, concluído ou cancelado.</summary>
    EventNotAcceptingCheckIn = 1,

    CheckInNotOpenYet,

    CheckInClosed,

    NotRegistered,

    /// <summary>A inscrição não tem vaga confirmada: está na espera, retirada ou encerrada.</summary>
    RegistrationNotConfirmed,

    /// <summary>A conta não está elegível agora (INV-01, revalidada no check-in).</summary>
    ParticipantIneligible,
}
