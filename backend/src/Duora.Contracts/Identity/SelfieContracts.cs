namespace Duora.Contracts.Identity;

public sealed record StartSelfieVerificationRequest;
public sealed record CompleteSelfieVerificationRequest;
public sealed record SelfieCaptureRequest;
public sealed record SelfieVerificationResponse(Guid? Id, string State, string NextStep, string? FeedbackCode, DateTime? ExpiresAt);
public sealed record SelfieCaptureCredentialsResponse(string AccessKeyId, string SecretAccessKey, string SessionToken, DateTime Expiration);
public sealed record SelfieCaptureResponse(string SessionId, string Region, SelfieCaptureCredentialsResponse Credentials);
