using System.Text.Json.Serialization;

namespace Duora.Contracts.Safety;

/// <summary>
/// Suspender uma conta por violação dos Termos (issue #299). <c>ReasonCode</c>: só <c>TermsViolation</c> nesta rota.
/// <c>InternalReason</c>: texto para a auditoria, de 1 a 2000 caracteres, que nunca chega à pessoa suspensa.
/// </summary>
public sealed record SuspendAccountRequest(string? ReasonCode, string? InternalReason);

/// <summary>Revogar uma suspensão; o motivo vai só para a auditoria (1 a 2000 caracteres).</summary>
public sealed record LiftSuspensionRequest(string? InternalReason);

/// <summary>
/// Uma suspensão, sem o motivo interno. <c>ReasonCode</c>: TermsViolation | SelfieVerificationFailed.
/// <c>ETag</c>: o valor de <c>If-Match</c> para revogá-la. Instantes em UTC, com "Z" (spec 8.1).
/// </summary>
public sealed record AccountSuspensionResponse(
    Guid SuspensionId,
    Guid UserId,
    string ReasonCode,
    bool DecidedAutomatically,
    DateTime SuspendedAt,
    DateTime? LiftedAt,
    [property: JsonPropertyName("eTag")] string ETag);

/// <summary>Estado de moderação de uma conta: a suspensão ativa, ou nula.</summary>
public sealed record AccountModerationStateResponse(Guid UserId, AccountSuspensionResponse? ActiveSuspension);
