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
    /// Candidato sanitizado privado que aguarda decisão humana, sem aprovação automática. Admitida em Production pela
    /// decisão do dono de 2026-10-28 (ADR-0035); ligar o upload continua sendo do inventário do host.
    /// </summary>
    HumanReview,
}

public static class MediaReview
{
    /// <summary>
    /// Allowlists por política. Um ambiente novo (Staging, um nome digitado errado) fica de fora até alguém decidir, em
    /// vez de herdar uma política. Só a revisão humana chega a Production: aprovação só técnica nunca serve a foto real.
    /// </summary>
    private static readonly string[] SyntheticDataEnvironments = ["Development", "Testing"];

    private static readonly string[] HumanReviewEnvironments = [.. SyntheticDataEnvironments, "Production"];

    public static bool IsPermitted(MediaReviewPolicy policy, string environmentName) => policy switch
    {
        MediaReviewPolicy.TechnicalOnly => SyntheticDataEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase),
        MediaReviewPolicy.HumanReview => HumanReviewEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase),
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Política de revisão desconhecida."),
    };

    public static UploadState VerdictAfterSanitizing(MediaReviewPolicy policy) => policy switch
    {
        MediaReviewPolicy.TechnicalOnly => UploadState.Approved,
        MediaReviewPolicy.HumanReview => UploadState.AwaitingReview,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Política de revisão desconhecida."),
    };
}
