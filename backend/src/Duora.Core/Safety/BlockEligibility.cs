using Duora.Core.Identity;

namespace Duora.Core.Safety;

/// <summary>
/// Quem pode bloquear (decisão G4 de 2026-10-15). Bloquear só reduz interação, então conta restrita ou suspensa
/// continua podendo se proteger; conta pendente ainda não conhece ninguém, e conta em exclusão já saiu.
/// </summary>
public static class BlockEligibility
{
    public static bool AllowsBlocking(AccountStatus status) => status switch
    {
        AccountStatus.Active or AccountStatus.Restricted or AccountStatus.Suspended => true,
        AccountStatus.PendingEmail or AccountStatus.PendingEligibility or AccountStatus.DeletionPending => false,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Estado de conta sem decisão de bloqueio."),
    };
}
