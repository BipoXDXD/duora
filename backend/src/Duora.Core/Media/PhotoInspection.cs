namespace Duora.Core.Media;

/// <summary>Motivo técnico de uma foto recusada pelo pipeline (spec 16.1). Não é decisão editorial.</summary>
public enum PhotoRejection
{
    /// <summary>Os bytes não são JPEG, PNG nem WebP (SVG, HTML, executável, GIF...).</summary>
    UnsupportedFormat,

    /// <summary>O formato real difere do <c>Content-Type</c> declarado.</summary>
    TypeMismatch,

    /// <summary>Mais de 20 megapixels segundo o header, antes de decodificar.</summary>
    TooManyPixels,

    /// <summary>Mais de um frame: APNG, WebP animado.</summary>
    Animated,

    /// <summary>Header ou dados que o decoder não aceita (truncado, dimensão zero, CRC inválido).</summary>
    CorruptImage,

    /// <summary>Estourou o tempo ou a memória da sanitização.</summary>
    ProcessingLimit,
}

/// <summary>Códigos estáveis das rejeições, gravados no banco e devolvidos em API-M02.</summary>
public static class PhotoRejections
{
    public static string Code(PhotoRejection rejection) => rejection switch
    {
        PhotoRejection.UnsupportedFormat => "UNSUPPORTED_FORMAT",
        PhotoRejection.TypeMismatch => "TYPE_MISMATCH",
        PhotoRejection.TooManyPixels => "TOO_MANY_PIXELS",
        PhotoRejection.Animated => "ANIMATED",
        PhotoRejection.CorruptImage => "CORRUPT_IMAGE",
        PhotoRejection.ProcessingLimit => "PROCESSING_LIMIT",
        _ => throw new ArgumentOutOfRangeException(nameof(rejection), rejection, "Rejeição sem código."),
    };

    public static PhotoRejection? FromCode(string code) =>
        Enum.GetValues<PhotoRejection>().Select(r => (PhotoRejection?)r).FirstOrDefault(r => Code(r!.Value) == code);
}

/// <summary>
/// Regras puras da inspeção (spec 4.3 e 16.1), na ordem do pipeline: assinatura, depois o header. Cada passo devolve
/// a rejeição ou <c>null</c> para seguir; o Worker só decodifica o que passou pelas duas.
/// </summary>
public static class PhotoInspection
{
    /// <summary>Foto: até 20 megapixels decodificados (spec 4.3).</summary>
    public const long MaxPixels = 20_000_000;

    public static PhotoRejection? CheckSignature(PhotoFormat declared, PhotoFormat? detected) => detected switch
    {
        null => PhotoRejection.UnsupportedFormat,
        var real when real != declared => PhotoRejection.TypeMismatch,
        _ => null,
    };

    /// <summary>
    /// Dimensões e frames lidos do header, antes de alocar os pixels. Zero frames quer dizer que o decoder não os
    /// conta pelo header (o do JPEG não conta); a animação também é checada pela estrutura do arquivo.
    /// </summary>
    public static PhotoRejection? CheckHeader(int width, int height, int frameCount)
    {
        if (width <= 0 || height <= 0 || frameCount < 0)
        {
            return PhotoRejection.CorruptImage;
        }

        if ((long)width * height > MaxPixels)
        {
            return PhotoRejection.TooManyPixels;
        }

        return frameCount > 1 ? PhotoRejection.Animated : null;
    }
}
