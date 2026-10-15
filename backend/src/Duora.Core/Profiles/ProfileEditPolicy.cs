using Duora.Core.Identity;

namespace Duora.Core.Profiles;

/// <summary>
/// Quem pode alterar o próprio perfil. A spec 8.2 limita a sessão restrita ao estado da própria conta, à segurança e
/// ao recurso; o perfil é o que outras pessoas veem, então conta restrita, suspensa ou em exclusão só o lê.
/// </summary>
public static class ProfileEditPolicy
{
    public static bool Allows(AccountStatus status) => status switch
    {
        AccountStatus.PendingEligibility or AccountStatus.Active => true,
        AccountStatus.PendingEmail
            or AccountStatus.Restricted
            or AccountStatus.Suspended
            or AccountStatus.DeletionPending => false,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Estado de conta sem decisão de edição."),
    };
}
