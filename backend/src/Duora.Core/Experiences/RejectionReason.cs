namespace Duora.Core.Experiences;

/// <summary>Motivos estáveis de recusa de uma ação; a API os traduz em códigos de erro (spec 8.6).</summary>
public enum RejectionReason
{
    NotParticipant = 1,
    SessionFinished = 2,
    DeadlineReached = 3,
    ActionIdReused = 4,
    WrongStage = 5,
    NotYourTurn = 6,
    StaleStep = 7,
    UnknownOption = 8,
    AlreadyActed = 9,
    RevisionLimitReached = 10,
}
