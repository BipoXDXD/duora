namespace Duora.Contracts.Identity;

public sealed record StartSelfieVerificationRequest;
public sealed record CompleteSelfieVerificationRequest;
public sealed record SelfieCaptureRequest;

/// <summary>
/// Situação da selfie. <c>FeedbackCode</c>: código aberto e opcional, sem texto do fornecedor. <c>CAPTURE_QUALITY</c> e
/// <c>PRESENCE_INCONCLUSIVE</c> vêm de um resultado lido; <c>PROVIDER_UNAVAILABLE</c> diz que a verificação não pôde
/// ser feita por falha do fornecedor ou por atraso interno, e não conta como reprovação. Na tentativa aberta, sai
/// quando o fornecedor responde; na tentativa expirada, fica. Valor desconhecido deve ser ignorado.
/// </summary>
public sealed record SelfieVerificationResponse(
    Guid? Id,
    string State,
    string NextStep,
    string? FeedbackCode,
    DateTime? ExpiresAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SelfieAttemptsResponse? Attempts);

/// <summary>
/// Limite de reprovações da selfie (issue #300). <c>State</c>: Available | CoolingDown | Exhausted.
/// <c>Remaining</c>: reprovações que ainda cabem no ciclo (0 fora de Available). <c>LastCycle</c>: esgotar este ciclo
/// suspende a conta. <c>LockedUntil</c>: só em CoolingDown, instante UTC em que o segundo ciclo abre.
/// </summary>
public sealed record SelfieAttemptsResponse(string State, int Remaining, bool LastCycle, DateTime? LockedUntil);
public sealed record SelfieCaptureCredentialsResponse(string AccessKeyId, string SecretAccessKey, string SessionToken, DateTime Expiration);
public sealed record SelfieCaptureResponse(string SessionId, string Region, SelfieCaptureCredentialsResponse Credentials);
