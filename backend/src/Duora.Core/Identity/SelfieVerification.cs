namespace Duora.Core.Identity;

/// <summary>Qualidade e presença; nenhum estado representa idade ou identidade civil.</summary>
public enum SelfieState
{
    Pending,
    Capturing,
    Processing,
    Passed,
    RecaptureRequired,
    Inconclusive,
    Expired,
}

/// <summary>Códigos de <c>feedback_code</c> que nascem no produto, e não na resposta do fornecedor.</summary>
public static class SelfieFeedbackCodes
{
    /// <summary>
    /// O fornecedor falhou ou não houve tempo de chamá-lo: a captura não foi julgada, não conta como reprovação e nada
    /// é liberado. Não prova queda: o transitório inclui toda exceção do SDK, até credencial errada.
    /// </summary>
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
}

public sealed record SelfieSession(string Reference, DateTimeOffset ExpiresAt);

public sealed record SelfieResult(SelfieState State, string? FeedbackCode, string PolicyVersion);

public interface ISelfieProvider
{
    Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken);

    Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>Decisão de participação independente de qualquer resultado etário histórico.</summary>
public static class ParticipationEligibility
{
    public static bool CanParticipate(AccountStatus status, bool emailConfirmed, SelfieState? selfie) =>
        status == AccountStatus.Active && emailConfirmed && selfie == SelfieState.Passed;

    /// <summary>
    /// Estado da conta depois da prova de presença: a conta em <c>PendingEligibility</c> sobe a <c>Active</c> quando,
    /// ativa, já poderia participar (e-mail confirmado e selfie aprovada). Qualquer outro estado fica como está.
    /// </summary>
    public static AccountStatus AfterPresenceCheck(AccountStatus status, bool emailConfirmed, SelfieState? selfie) =>
        status == AccountStatus.PendingEligibility && CanParticipate(AccountStatus.Active, emailConfirmed, selfie)
            ? AccountStatus.Active
            : status;
}
