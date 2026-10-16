using System.Text.RegularExpressions;

namespace Duora.Infrastructure.Audit;

/// <summary>Quem fez a ação auditada (coluna `actor_kind`, gravada pelo nome).</summary>
internal enum AuditActorKind
{
    User,
    Moderator,
    Admin,
    Operator,
    System,
}

/// <summary>
/// Catálogo fechado das ações auditadas (model.md, `ops.audit_events`). Ação nova entra aqui, com o código no
/// formato `módulo.fato_no_passado`; texto livre não chega à coluna.
/// </summary>
internal sealed record AuditAction
{
    public static readonly AuditAction SafetyBlockCreated = new("safety.block_created");

    public static readonly AuditAction MfaEnrollmentStarted = new("identity.mfa_enrollment_started");

    public static readonly AuditAction MfaEnabled = new("identity.mfa_enabled");

    public static readonly AuditAction MfaDisabled = new("identity.mfa_disabled");

    public static readonly AuditAction MfaRecoveryCodesRegenerated = new("identity.mfa_recovery_codes_regenerated");

    public static readonly AuditAction MfaRecoveryCodeUsed = new("identity.mfa_recovery_code_used");

    /// <summary>Senha certa e segundo fator errado (conta no bloqueio; sinal de senha vazada).</summary>
    public static readonly AuditAction SecondFactorFailed = new("identity.second_factor_failed");

    public static readonly AuditAction Reauthenticated = new("identity.reauthenticated");

    public static readonly AuditAction PasswordChanged = new("identity.password_changed");

    /// <summary>Senha redefinida pelo link de recuperação (API-A07).</summary>
    public static readonly AuditAction PasswordReset = new("identity.password_reset");

    public static readonly AuditAction LoginSucceeded = new("identity.login_succeeded");

    /// <summary>Senha errada numa conta existente (conta no bloqueio). E-mail sem conta não é registrado.</summary>
    public static readonly AuditAction LoginFailed = new("identity.login_failed");

    /// <summary>Tentativa de login recusada sem conferir a senha, porque a conta está bloqueada.</summary>
    public static readonly AuditAction LoginRefusedLockedOut = new("identity.login_refused_locked_out");

    /// <summary>A falha que fez a conta atingir o teto e entrar no bloqueio temporário.</summary>
    public static readonly AuditAction AccountLockedOut = new("identity.account_locked_out");

    /// <summary>Bloqueio ativo encerrado antes do prazo (hoje, só pelo reset de senha de conta sem MFA).</summary>
    public static readonly AuditAction LockoutCleared = new("identity.lockout_cleared");

    public static readonly AuditAction LoggedOut = new("identity.logged_out");

    /// <summary>O titular encerrou uma sessão pela lista de sessões (API-A10).</summary>
    public static readonly AuditAction SessionRevoked = new("identity.session_revoked");

    public static readonly AuditAction AgeVerificationRequested = new("identity.age_verification_requested");

    public static readonly AuditAction AgeVerificationExpired = new("identity.age_verification_expired");

    public static readonly AuditAction AgeVerificationDecided = new("identity.age_verification_decided");

    public static readonly AuditAction AccountActivated = new("identity.account_activated");

    public static readonly AuditAction AccountRestricted = new("identity.account_restricted");

    public static readonly AuditAction AgeVerificationLateNotAdult = new("identity.age_verification_late_not_adult");

    private AuditAction(string code) => Code = code;

    public string Code { get; }
}

/// <summary>Tipos de alvo usados em <see cref="AuditRecord.TargetType"/>.</summary>
internal static class AuditTargetTypes
{
    public const string User = "user";

    public const string AgeVerification = "age_verification";

    public const string LoginSession = "login_session";
}

/// <summary>
/// Um fato de auditoria: ator, ação e alvo. Sem texto livre, senha, ticket nem corpo de conversa (spec 10.2); o
/// alvo é um id, e o tipo do alvo segue o formato de catálogo.
/// </summary>
internal sealed partial record AuditRecord
{
    public AuditRecord(
        AuditActorKind actorKind,
        Guid? actorId,
        AuditAction action,
        string targetType,
        Guid targetId,
        long? targetVersion = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(targetType);
        if (!Enum.IsDefined(actorKind))
        {
            throw new ArgumentOutOfRangeException(nameof(actorKind), actorKind, "Ator desconhecido.");
        }

        if (actorId == Guid.Empty || (actorKind != AuditActorKind.System && actorId is null))
        {
            throw new ArgumentException("Só o sistema age sem id de ator.", nameof(actorId));
        }

        if (!TargetTypeFormat().IsMatch(targetType))
        {
            throw new ArgumentException("Tipo de alvo fora do formato do catálogo.", nameof(targetType));
        }

        ArgumentOutOfRangeException.ThrowIfEqual(targetId, Guid.Empty);
        ActorKind = actorKind;
        ActorId = actorId;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        TargetVersion = targetVersion;
    }

    public AuditActorKind ActorKind { get; }

    public Guid? ActorId { get; }

    public AuditAction Action { get; }

    /// <summary>Tipo do alvo em minúsculas, como `user` ou `report`; até 32 caracteres.</summary>
    public string TargetType { get; }

    public Guid TargetId { get; }

    /// <summary>Versão do alvo depois da ação, quando ele tem uma.</summary>
    public long? TargetVersion { get; }

    [GeneratedRegex("^[a-z][a-z_]{0,31}$")]
    private static partial Regex TargetTypeFormat();
}
