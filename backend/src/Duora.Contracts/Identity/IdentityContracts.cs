namespace Duora.Contracts.Identity;

// DTOs de entrada da API de conta (spec 8.2). Todos os campos são anuláveis para que ausência e null cheguem até
// a validação e virem 400 com código estável; campo desconhecido é recusado pelo serializador.

public sealed record RegisterRequest(
    string? Email,
    string? Password,
    string? AcceptedTermsVersion,
    string? AcceptedPrivacyVersion);

public sealed record ConfirmEmailRequest(Guid? UserId, string? Token, string? Password);

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
    IReadOnlyList<string> Roles);

/// <summary>Cadastro do TOTP (API-A11): mostrado uma vez, com <c>Cache-Control: no-store</c>.</summary>
public sealed record MfaEnrollmentResponse(string SharedKey, string AuthenticatorUri);

/// <summary>Códigos de recuperação (API-A12 e A14): mostrados uma vez; o servidor guarda só o hash.</summary>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

public sealed record SessionResponse(Guid Id, DateTime CreatedAt, DateTime LastSeenAt, bool Current, string Description);

public sealed record SessionListResponse(IReadOnlyList<SessionResponse> Items);

/// <summary>
/// Corpo de API-P05: vazio de propósito. O resultado etário nunca vem do navegador (spec 12.2, TM-35); qualquer
/// campo, inclusive <c>isAdult</c>, é recusado com 400.
/// </summary>
public sealed record StartAgeVerificationRequest;

/// <summary>
/// Estado mínimo da verificação etária (API-P06). <c>State</c>: NotStarted | Pending | VerifiedAdult | Rejected |
/// Expired | ManualReview. <c>NextStep</c>: None | StartVerification | WaitForProvider | ContinueWithProvider |
/// WaitForReview | NotAvailable. <c>ContinueUrl</c> só vem com ContinueWithProvider: o fluxo do fornecedor, de uma
/// origem na allowlist. Nenhuma inferência do fornecedor (TM-38).
/// </summary>
public sealed record AgeVerificationResponse(string State, string NextStep, string? ContinueUrl);
