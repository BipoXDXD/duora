using System.Text.Json.Serialization;

namespace Duora.Contracts.Identity;

// DTOs de entrada da API de conta (spec 8.2). Todos os campos são anuláveis para que ausência e null cheguem até
// a validação e virem 400 com código estável; campo desconhecido é recusado pelo serializador.

public sealed record RegisterRequest(
    string? Email,
    string? Password,
    string? AcceptedTermsVersion,
    string? AcceptedPrivacyVersion);

public sealed record ConfirmEmailRequest(Guid? UserId, string? Token, string? Password);

public sealed record LoginRequest(string? Email, string? Password);

public sealed record CreatePasswordResetRequest(string? Email);

public sealed record ResetPasswordRequest(Guid? UserId, string? Token, string? NewPassword);

// Saídas. Instantes em UTC (DateTime com Kind Utc), serializados com "Z" (spec 8.1).

/// <summary>
/// Conta do titular. <c>Status</c>: PendingEmail | PendingEligibility | Active | Restricted | Suspended |
/// DeletionPending. <c>Roles</c> vem vazio para participante; papéis privilegiados chegam com o MFA (ADR-0017).
/// </summary>
public sealed record MeResponse(
    Guid Id,
    string Email,
    string Status,
    bool EmailConfirmed,
    bool MfaEnabled,
    IReadOnlyList<string> Roles);

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
