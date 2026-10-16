using Duora.Core.Identity;

namespace Duora.Core.Media;

/// <summary>
/// Quem pode enviar foto: as mesmas contas que podem editar o próprio perfil, porque a foto é parte do que os outros
/// veem. Conta restrita, suspensa ou em exclusão não publica nada novo (spec 8.2 e 12.3).
/// </summary>
public static class PhotoUploadPolicy
{
    public static bool Allows(AccountStatus status) => status switch
    {
        AccountStatus.PendingEligibility or AccountStatus.Active => true,
        AccountStatus.PendingEmail
            or AccountStatus.Restricted
            or AccountStatus.Suspended
            or AccountStatus.DeletionPending => false,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Estado de conta sem decisão de upload."),
    };
}
