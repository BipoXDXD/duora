namespace Duora.Core.Notifications;

/// <summary>Mudança de credencial que a conta avisa ao titular por e-mail (TM-21; spec 8.2).</summary>
public enum SecurityChange
{
    MfaEnabled,
    MfaDisabled,
    RecoveryCodesRegenerated,
    PasswordChanged,

    /// <summary>Senha trocada pelo link de recuperação (API-A07), não pela própria pessoa logada (A16).</summary>
    PasswordReset,
}
