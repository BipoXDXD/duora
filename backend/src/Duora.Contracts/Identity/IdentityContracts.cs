using System.Text.Json.Serialization;

namespace Duora.Contracts.Identity;

// DTOs de entrada da API de conta (spec 8.2). Todos os campos são anuláveis para que ausência e null cheguem até
// a validação e virem 400 com código estável; campo desconhecido é recusado pelo serializador.

public sealed record RegisterRequest(
    string? Email,
    string? Password,
    string? AcceptedTermsVersion,
    string? AcceptedPrivacyVersion,
    string? AcceptedTermsLocale = null,
    string? AcceptedPrivacyLocale = null);

public sealed record ConfirmEmailRequest(Guid? UserId, string? Token, string? Password);

/// <summary>Pedido explícito e neutro de outro código; preserva senha, aceites e prazo do cadastro pendente.</summary>
public sealed record CreateEmailConfirmationRequest(string? Email, string? Password);

/// <summary>
/// Login (API-A04). Conta com MFA ativo manda também <c>twoFactorCode</c> (6 dígitos do autenticador) ou
/// <c>recoveryCode</c>, nunca os dois.
/// </summary>
public sealed record LoginRequest(string? Email, string? Password, string? TwoFactorCode = null, string? RecoveryCode = null);

/// <summary>Reautenticação (API-A13): senha e, se a conta tem MFA, o código do autenticador.</summary>
public sealed record ReauthenticateRequest(string? Password, string? TwoFactorCode = null);

/// <summary>Prova de posse do autenticador que ativa o MFA (API-A12).</summary>
public sealed record ConfirmMfaRequest(string? Code);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record CreatePasswordResetRequest(string? Email);

public sealed record ResetPasswordRequest(Guid? UserId, string? Token, string? NewPassword);

// Saídas. Instantes em UTC (DateTime com Kind Utc), serializados com "Z" (spec 8.1).

/// <summary>
/// Conta do titular. <c>Status</c>: PendingEmail | PendingEligibility | Active | Restricted | Suspended |
/// DeletionPending. <c>Roles</c>: nomes do catálogo (participant, moderator, product_admin, operator); vazio para
/// participante que nunca recebeu papel explícito.
/// </summary>
public sealed record MeResponse(
    Guid Id,
    string Email,
    string Status,
    bool EmailConfirmed,
    bool MfaEnabled,
    IReadOnlyList<string> Roles,
    PrivateSessionContextResponse SessionContext,
    string? PreferredLocale = null,
    string? PreferredTimeZone = null);

/// <summary>Contexto do titular, sem credencial portadora; a prova antiforgery fica só na memória da SPA.</summary>
public sealed record PrivateSessionContextResponse(Guid UserId, Guid SessionId, string Phase, string RequestToken);

/// <summary>403 somente do bootstrap /me na fase da senha temporária; não amplia as permissões da sessão.</summary>
public sealed record PrivateSessionRestrictionResponse(
    string Type, string Title, int Status, string Code, PrivateSessionContextResponse SessionContext, string? TraceId = null);

/// <summary>Cadastro do TOTP (API-A11): mostrado uma vez, com <c>Cache-Control: no-store</c>.</summary>
public sealed record MfaEnrollmentResponse(string SharedKey, string AuthenticatorUri);

/// <summary>Códigos de recuperação (API-A12 e A14): mostrados uma vez; o servidor guarda só o hash.</summary>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes, Guid ResultSessionId);

public sealed record SessionResponse(Guid Id, DateTime CreatedAt, DateTime LastSeenAt, bool Current, string Description);

public sealed record SessionListResponse(IReadOnlyList<SessionResponse> Items);

/// <summary>
/// Corpo de API-P05: vazio de propósito. O resultado etário nunca vem do navegador (spec 12.2, TM-35); qualquer
/// campo, inclusive <c>isAdult</c>, é recusado com 400.
/// </summary>
public sealed record StartAgeVerificationRequest;

/// <summary>Estado da verificação etária como a API o expõe (API-P06).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgeVerificationStateCode>))]
public enum AgeVerificationStateCode
{
    NotStarted,
    Pending,
    VerifiedAdult,
    Rejected,
    Expired,
    ManualReview,
}

/// <summary>Próximo passo da pessoa na verificação etária (API-P06).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgeVerificationNextStepCode>))]
public enum AgeVerificationNextStepCode
{
    None,
    StartVerification,
    WaitForProvider,
    ContinueWithProvider,
    WaitForReview,
    NotAvailable,
}

/// <summary>
/// Estado mínimo da verificação etária (API-P06). <c>ContinueUrl</c> só vem com ContinueWithProvider: o fluxo do
/// fornecedor, de uma origem na allowlist. Nenhuma inferência do fornecedor (TM-38).
/// </summary>
public sealed record AgeVerificationResponse(
    AgeVerificationStateCode State,
    AgeVerificationNextStepCode NextStep,
    string? ContinueUrl);
