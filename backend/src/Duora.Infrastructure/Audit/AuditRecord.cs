using System.Text.RegularExpressions;
using Duora.Core.Identity;

namespace Duora.Infrastructure.Audit;

/// <summary>Quem fez a ação auditada (coluna `actor_kind`, gravada pelo nome).</summary>
internal enum AuditActorKind
{
    User,
    Moderator,
    Admin,
    Operator,

    /// <summary>
    /// Quem rodou um comando operacional no host, sem conta no produto (o bootstrap cria a primeira). Não é o papel
    /// <c>operator</c>: identifica-se só pelo rótulo <c>--operator</c>, nunca por id.
    /// </summary>
    TerminalOperator,
    System,
}

/// <summary>
/// Catálogo fechado das ações auditadas (model.md, `ops.audit_events`). Ação nova entra aqui, com o código no
/// formato `módulo.fato_no_passado`; texto livre não chega à coluna.
/// </summary>
internal sealed record AuditAction
{
    public static readonly AuditAction SafetyBlockCreated = new("safety.block_created");

    public static readonly AuditAction PhotoReviewRequested = new("safety.photo_review_requested");

    public static readonly AuditAction PhotoReviewAssigned = new("safety.photo_review_assigned");

    public static readonly AuditAction PhotoReviewEvidenceViewed = new("safety.photo_review_evidence_viewed");
    public static readonly AuditAction PhotoReviewAppealEvidenceViewed = new("safety.photo_review_appeal_evidence_viewed");

    public static readonly AuditAction PhotoReviewApproved = new("safety.photo_review_approved");

    public static readonly AuditAction PhotoReviewRejected = new("safety.photo_review_rejected");

    public static readonly AuditAction PhotoReviewExpired = new("safety.photo_review_expired");
    public static readonly AuditAction PhotoReviewUnavailable = new("safety.photo_review_unavailable");

    public static readonly AuditAction PhotoReviewAppealRequested = new("safety.photo_review_appeal_requested");

    public static readonly AuditAction PhotoReviewAppealUnavailable = new("safety.photo_review_appeal_unavailable");

    public static readonly AuditAction PhotoReviewAppealApproved = new("safety.photo_review_appeal_approved");

    public static readonly AuditAction PhotoReviewAppealRejected = new("safety.photo_review_appeal_rejected");

    public static readonly AuditAction PhotoReviewAppealAssigned = new("safety.photo_review_appeal_assigned");

    public static readonly AuditAction PhotoReviewPermissionGranted = new("safety.photo_review_permission_granted");

    public static readonly AuditAction PhotoReviewPermissionRevoked = new("safety.photo_review_permission_revoked");

    /// <summary>O titular excluiu a própria foto (API-M03); o alvo é a foto, com a versão resultante.</summary>
    public static readonly AuditAction MediaPhotoDeleted = new("media.photo_deleted");

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

    /// <summary>Senha errada numa conta confirmada (conta no bloqueio).</summary>
    public static readonly AuditAction LoginFailed = new("identity.login_failed");

    /// <summary>Senha certa numa conta com MFA, sem segundo fator no pedido (<c>MFA_REQUIRED</c>).</summary>
    public static readonly AuditAction LoginSecondFactorRequired = new("identity.login_second_factor_required");

    /// <summary>Tentativa de login recusada sem conferir a senha, porque a conta está bloqueada.</summary>
    public static readonly AuditAction LoginRefusedLockedOut = new("identity.login_refused_locked_out");

    /// <summary>Senha certa na reautenticação de conta com MFA, sem o código do autenticador.</summary>
    public static readonly AuditAction ReauthenticationSecondFactorRequired =
        new("identity.reauthentication_second_factor_required");

    /// <summary>Senha errada na reautenticação (API-A13; conta no bloqueio).</summary>
    public static readonly AuditAction ReauthenticationFailed = new("identity.reauthentication_failed");

    /// <summary>Reautenticação recusada sem conferir a senha, porque a conta está bloqueada.</summary>
    public static readonly AuditAction ReauthenticationRefusedLockedOut =
        new("identity.reauthentication_refused_locked_out");

    /// <summary>Senha atual errada na troca de senha (API-A16; conta no bloqueio).</summary>
    public static readonly AuditAction PasswordChangeFailed = new("identity.password_change_failed");

    /// <summary>Troca de senha recusada sem conferir a senha atual, porque a conta está bloqueada.</summary>
    public static readonly AuditAction PasswordChangeRefusedLockedOut =
        new("identity.password_change_refused_locked_out");

    /// <summary>A falha que fez a conta atingir o teto e entrar no bloqueio temporário.</summary>
    public static readonly AuditAction AccountLockedOut = new("identity.account_locked_out");

    /// <summary>Bloqueio ativo encerrado antes do prazo (hoje, só pelo reset de senha de conta sem MFA).</summary>
    public static readonly AuditAction LockoutCleared = new("identity.lockout_cleared");

    public static readonly AuditAction LoggedOut = new("identity.logged_out");

    /// <summary>O titular encerrou uma sessão pela lista de sessões (API-A10).</summary>
    public static readonly AuditAction SessionRevoked = new("identity.session_revoked");

    /// <summary>Conta privilegiada criada pelo comando operacional (<c>admin grant</c>, spec 11.1), uma ação por papel.</summary>
    public static readonly AuditAction AdminGrantedModerator = new("identity.admin_granted.moderator");

    public static readonly AuditAction AdminGrantedProductAdmin = new("identity.admin_granted.product_admin");

    public static readonly AuditAction AdminGrantedOperator = new("identity.admin_granted.operator");

    /// <summary>Senha temporária nova para a conta do bootstrap que ainda não ativou o MFA (<c>--reissue</c>).</summary>
    public static readonly AuditAction AdminTemporaryPasswordReissued = new("identity.admin_temporary_password_reissued");

    public static readonly AuditAction AgeVerificationRequested = new("identity.age_verification_requested");

    public static readonly AuditAction AgeVerificationExpired = new("identity.age_verification_expired");

    public static readonly AuditAction AgeVerificationDecided = new("identity.age_verification_decided");

    public static readonly AuditAction AccountActivated = new("identity.account_activated");

    public static readonly AuditAction AccountRestricted = new("identity.account_restricted");

    public static readonly AuditAction AgeVerificationLateNotAdult = new("identity.age_verification_late_not_adult");

    private AuditAction(string code) => Code = code;

    public string Code { get; }

    /// <summary>A concessão do papel privilegiado: o papel fica na ação, sem coluna nova nem texto livre.</summary>
    /// <exception cref="ArgumentOutOfRangeException">O papel não é privilegiado.</exception>
    public static AuditAction AdminGranted(string role) => role switch
    {
        AccountRoles.Moderator => AdminGrantedModerator,
        AccountRoles.ProductAdmin => AdminGrantedProductAdmin,
        AccountRoles.Operator => AdminGrantedOperator,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Papel sem ação de concessão."),
    };
}

/// <summary>Tipos de alvo usados em <see cref="AuditRecord.TargetType"/>.</summary>
internal static class AuditTargetTypes
{
    public const string User = "user";

    public const string AgeVerification = "age_verification";

    public const string LoginSession = "login_session";

    public const string PhotoReviewCase = "photo_review_case";

    public const string PhotoReviewEvidenceAccess = "photo_review_access";

    public const string PhotoReviewDecision = "photo_review_decision";

    public const string PhotoReviewAppeal = "photo_review_appeal";

    public const string PhotoReviewGrant = "photo_review_grant";

    public const string Photo = "photo";
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
        : this(actorKind, actorId, actorLabel: null, action, targetType, targetId, targetVersion)
    {
    }

    /// <summary>
    /// Fato registrado por quem rodou um comando no host, sem conta no produto (o bootstrap cria a primeira). O rótulo
    /// vem do argumento obrigatório <c>--operator</c>: é o que quem rodou declarou, não uma identidade verificada; quem
    /// responde pelo comando é quem tem acesso ao host.
    /// </summary>
    public static AuditRecord ByTerminalOperator(
        OperatorName operatorName,
        AuditAction action,
        string targetType,
        Guid targetId)
    {
        ArgumentNullException.ThrowIfNull(operatorName);
        return new AuditRecord(AuditActorKind.TerminalOperator, actorId: null, operatorName, action, targetType, targetId, targetVersion: null);
    }

    private AuditRecord(
        AuditActorKind actorKind,
        Guid? actorId,
        OperatorName? actorLabel,
        AuditAction action,
        string targetType,
        Guid targetId,
        long? targetVersion)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(targetType);
        if (!Enum.IsDefined(actorKind))
        {
            throw new ArgumentOutOfRangeException(nameof(actorKind), actorKind, "Ator desconhecido.");
        }

        if ((actorKind == AuditActorKind.TerminalOperator) != (actorLabel is not null))
        {
            throw new ArgumentException("Só o operador de terminal tem rótulo, e ele sempre o tem.", nameof(actorKind));
        }

        var actsWithoutId = actorKind is AuditActorKind.System or AuditActorKind.TerminalOperator;
        if (actorId == Guid.Empty
            || (actorId is null && !actsWithoutId)
            || (actorId is not null && actorKind == AuditActorKind.TerminalOperator))
        {
            throw new ArgumentException("Só o sistema e o operador de terminal agem sem id de ator.", nameof(actorId));
        }

        if (!TargetTypeFormat().IsMatch(targetType))
        {
            throw new ArgumentException("Tipo de alvo fora do formato do catálogo.", nameof(targetType));
        }

        ArgumentOutOfRangeException.ThrowIfEqual(targetId, Guid.Empty);
        ActorKind = actorKind;
        ActorId = actorId;
        ActorLabel = actorLabel;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        TargetVersion = targetVersion;
    }

    public AuditActorKind ActorKind { get; }

    public Guid? ActorId { get; }

    public OperatorName? ActorLabel { get; }

    public AuditAction Action { get; }

    /// <summary>Tipo do alvo em minúsculas, como `user` ou `report`; até 32 caracteres.</summary>
    public string TargetType { get; }

    public Guid TargetId { get; }

    /// <summary>Versão do alvo depois da ação, quando ele tem uma.</summary>
    public long? TargetVersion { get; }

    [GeneratedRegex("^[a-z][a-z_]{0,31}$")]
    private static partial Regex TargetTypeFormat();
}
