namespace Duora.Contracts.Identity;

// DTOs de entrada da API de conta (spec 8.2). Todos os campos são anuláveis para que ausência e null cheguem até
// a validação e virem 400 com código estável; campo desconhecido é recusado pelo serializador.

public sealed record RegisterRequest(
    string? Email,
    string? Password,
    string? AcceptedTermsVersion,
    string? AcceptedPrivacyVersion);

public sealed record ConfirmEmailRequest(Guid? UserId, string? Token);

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
