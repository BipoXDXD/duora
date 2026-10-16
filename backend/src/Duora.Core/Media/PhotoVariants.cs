namespace Duora.Core.Media;

/// <summary>Derivados publicáveis de uma foto aprovada (model.md, <c>media.media_variants.kind</c>).</summary>
public enum PhotoVariant
{
    Thumb,
    Card,
    Full,
}

/// <summary>Tamanhos dos derivados. O original nunca é servido; o maior derivado já é menor que ele em geral.</summary>
public static class PhotoVariants
{
    public static IReadOnlyList<PhotoVariant> All { get; } = Enum.GetValues<PhotoVariant>();

    /// <summary>Maior lado em pixels; hipótese de layout (avatar, cartão, tela cheia), revisável.</summary>
    public static int MaxSide(PhotoVariant variant) => variant switch
    {
        PhotoVariant.Thumb => 256,
        PhotoVariant.Card => 768,
        PhotoVariant.Full => 1600,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Variante sem tamanho."),
    };

    public static string Name(PhotoVariant variant) => variant switch
    {
        PhotoVariant.Thumb => "thumb",
        PhotoVariant.Card => "card",
        PhotoVariant.Full => "full",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Variante sem nome."),
    };

    /// <summary>Reduz mantendo a proporção até o maior lado caber; nunca amplia; nenhum lado fica abaixo de 1.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxSide)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSide, 1);

        var longest = Math.Max(width, height);
        if (longest <= maxSide)
        {
            return (width, height);
        }

        return (Scale(width, maxSide, longest), Scale(height, maxSide, longest));
    }

    private static int Scale(int side, int maxSide, int longest) =>
        (int)Math.Max(1, Math.Round((double)side * maxSide / longest, MidpointRounding.AwayFromZero));
}
