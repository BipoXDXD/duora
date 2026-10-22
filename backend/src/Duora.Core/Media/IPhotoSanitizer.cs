namespace Duora.Core.Media;

/// <summary>Derivado reencodado, pronto para o storage: sem metadados, orientação aplicada, em sRGB.</summary>
public sealed record SanitizedVariant(PhotoVariant Kind, byte[] Content, int Width, int Height);

public abstract record SanitizeOutcome
{
    /// <summary>Passou por todas as checagens; <c>Width</c> e <c>Height</c> são do original já orientado.</summary>
    public sealed record Sanitized(PhotoFormat Detected, int Width, int Height, IReadOnlyList<SanitizedVariant> Variants)
        : SanitizeOutcome;

    public sealed record Rejected(PhotoRejection Reason) : SanitizeOutcome;
}

/// <summary>
/// Porta da sanitização (ADR-0036). O Worker transporta bytes para o executável isolado, único processo que carrega
/// o decoder. Rejeição técnica é resultado; falha de transporte segue retry limitado, sem fallback local.
/// </summary>
public interface IPhotoSanitizer
{
    /// <summary>Formato dos derivados.</summary>
    const string VariantContentType = "image/webp";

    Task<SanitizeOutcome> SanitizeAsync(PhotoFormat declared, ReadOnlyMemory<byte> original, CancellationToken cancellationToken);
}
