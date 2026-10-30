namespace Duora.Contracts.Identity;

public sealed record StartSelfieVerificationRequest;
public sealed record CompleteSelfieVerificationRequest;
public sealed record SelfieCaptureRequest;
public sealed record SelfieVerificationResponse(
    Guid? Id,
    string State,
    string NextStep,
    string? FeedbackCode,
    DateTime? ExpiresAt,
    SelfieAttemptsResponse Attempts);

/// <summary>
/// Limite de reprovações da selfie (issue #300). <c>State</c>: Available | CoolingDown | Exhausted.
/// <c>Remaining</c>: reprovações que ainda cabem no ciclo (0 fora de Available). <c>LastCycle</c>: esgotar este ciclo
/// suspende a conta. <c>LockedUntil</c>: só em CoolingDown, instante UTC em que o segundo ciclo abre.
/// </summary>
public sealed record SelfieAttemptsResponse(string State, int Remaining, bool LastCycle, DateTime? LockedUntil);
public sealed record SelfieCaptureCredentialsResponse(string AccessKeyId, string SecretAccessKey, string SessionToken, DateTime Expiration);
public sealed record SelfieCaptureResponse(string SessionId, string Region, SelfieCaptureCredentialsResponse Credentials);
