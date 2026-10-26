namespace Duora.Core.Media;

/// <summary>Como uma foto sanitizada chega a <c>Approved</c> (ADR-0025, spec 12.3).</summary>
public enum MediaReviewPolicy
{
    /// <summary>
    /// Só a checagem técnica do pipeline. Serve para dados sintéticos: sem revisão editorial, nenhuma foto real
    /// pode depender dela, por isso o boot a recusa em <c>Production</c>.
    /// </summary>
    TechnicalOnly,

    /// <summary>
    /// Candidato sanitizado privado que aguarda decisão humana. Qualificado apenas com dados sintéticos nesta
    /// etapa; a admissão em Production/Staging depende dos aceites operacionais do ADR-0035.
    /// </summary>
    HumanReview,
}

public static class MediaReview
{
    /// <summary>
    /// Allowlist: só os ambientes de dados sintéticos. Um ambiente novo (Staging, um nome digitado errado) fica de fora
    /// até alguém decidir, em vez de herdar a aprovação sem revisão.
    /// </summary>
    private static readonly string[] SyntheticDataEnvironments = ["Development", "Testing"];

    public static bool IsPermitted(MediaReviewPolicy policy, string environmentName) => policy switch
    {
        MediaReviewPolicy.TechnicalOnly or MediaReviewPolicy.HumanReview =>
            SyntheticDataEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase),
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Política de revisão desconhecida."),
    };

    public static UploadState VerdictAfterSanitizing(MediaReviewPolicy policy) => policy switch
    {
        MediaReviewPolicy.TechnicalOnly => UploadState.Approved,
        MediaReviewPolicy.HumanReview => UploadState.AwaitingReview,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Política de revisão desconhecida."),
    };
}
