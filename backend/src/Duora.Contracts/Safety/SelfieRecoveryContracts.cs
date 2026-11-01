namespace Duora.Contracts.Safety;

/// <summary>Opaque incident reference (ASCII letters, digits, _ and -, 1–64) and private human rationale (1–2000).</summary>
public sealed record RecoverSelfieSuspensionRequest(string? IncidentReference, string? InternalReason);

/// <summary>Private administrative diagnosis; missing retained linkage is explicit and cannot authorize recovery.</summary>
public sealed record SelfieRecoveryDiagnosisResponse(Guid DeliveryId, Guid EventId, Guid VerificationId,
    Guid? SubjectUserId, string Standing, DateTime? ReviewedAt, Guid? SuspensionId, string? SuspensionReason);

/// <summary>Durable result of the exact command, including a replay after retention or human lift.</summary>
public sealed record SelfieRecoveryResponse(Guid DeliveryId, string Disposition, Guid? SuspensionId,
    DateTime RecordedAt, bool Replayed);
