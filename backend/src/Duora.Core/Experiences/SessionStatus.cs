namespace Duora.Core.Experiences;

/// <summary>
/// Estados de jogo da máquina de estados da sala (spec 4.2, ADR-0016). <c>Invited</c>, <c>Ready</c> e
/// <c>Aborted</c> pertencem ao fluxo de convite e saída, fora deste motor.
/// </summary>
public enum SessionStatus
{
    Active = 1,
    Completed = 2,
    Expired = 3,
}
