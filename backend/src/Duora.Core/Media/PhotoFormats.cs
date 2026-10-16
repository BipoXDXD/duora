namespace Duora.Core.Media;

/// <summary>Formatos aceitos para foto (spec 16.1): só JPEG, PNG e WebP estáticos.</summary>
public enum PhotoFormat
{
    Jpeg,
    Png,
    WebP,
}

/// <summary>
/// Tipo declarado e tipo real de uma foto. O declarado vem do <c>Content-Type</c> e só serve para conferir o real:
/// quem decide é a assinatura dos primeiros bytes (TM-41), nunca a extensão nem o nome, que a API nem recebe.
/// </summary>
public static class PhotoFormats
{
    /// <summary>Bytes que bastam para reconhecer qualquer formato aceito (o RIFF/WebP é o mais longo).</summary>
    public const int SignatureLength = 12;

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string ContentType(PhotoFormat format) => format switch
    {
        PhotoFormat.Jpeg => "image/jpeg",
        PhotoFormat.Png => "image/png",
        PhotoFormat.WebP => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato sem tipo de mídia."),
    };

    /// <summary>
    /// Só o tipo exato, sem parâmetros: <c>image/jpeg; charset=…</c> ou <c>image/jpg</c> não são o que a API
    /// documenta e indicam um cliente que não é o nosso.
    /// </summary>
    public static bool TryParseContentType(string? value, out PhotoFormat format)
    {
        foreach (var candidate in Enum.GetValues<PhotoFormat>())
        {
            if (string.Equals(value, ContentType(candidate), StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        return false;
    }

    /// <summary>Formato pela assinatura no início do arquivo; <c>null</c> para qualquer outra coisa.</summary>
    public static PhotoFormat? Sniff(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(JpegSignature))
        {
            return PhotoFormat.Jpeg;
        }

        if (header.StartsWith(PngSignature))
        {
            return PhotoFormat.Png;
        }

        return IsWebP(header) ? PhotoFormat.WebP : null;
    }

    private static bool IsWebP(ReadOnlySpan<byte> header) =>
        header.Length >= SignatureLength && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);
}
