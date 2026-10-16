namespace Duora.Core.Media;

/// <summary>Como uma foto sanitizada chega a <c>Approved</c> (ADR-0025, spec 12.3).</summary>
public enum MediaReviewPolicy
{
    /// <summary>
    /// Só a checagem técnica do pipeline. Serve para dados sintéticos: sem revisão editorial, nenhuma foto real
    /// pode depender dela, por isso o boot a recusa em <c>Production</c>.
    /// </summary>
    TechnicalOnly,
}

public static class MediaReview
{
    private const string ProductionEnvironment = "Production";

    public static bool IsPermitted(MediaReviewPolicy policy, string environmentName) => policy switch
    {
        MediaReviewPolicy.TechnicalOnly =>
            !string.Equals(environmentName, ProductionEnvironment, StringComparison.OrdinalIgnoreCase),
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Política de revisão desconhecida."),
    };

    public static UploadState VerdictAfterSanitizing(MediaReviewPolicy policy) => policy switch
    {
        MediaReviewPolicy.TechnicalOnly => UploadState.Approved,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Política de revisão desconhecida."),
    };
}
