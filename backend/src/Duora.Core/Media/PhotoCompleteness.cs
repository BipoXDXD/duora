using System.Buffers.Binary;

namespace Duora.Core.Media;

/// <summary>
/// Confere se o arquivo chegou inteiro pela estrutura do formato. Os decoders de JPEG e WebP são lenientes com arquivo
/// truncado (completam a imagem com cinza), e um upload cortado não pode virar foto aprovada (T-16).
/// </summary>
public static class PhotoCompleteness
{
    private const int PngSignatureLength = 8;
    private const int PngChunkOverhead = 12;
    private const int RiffHeaderLength = 8;
    private const byte Marker = 0xFF;
    private const byte StartOfScan = 0xDA;
    private const byte EndOfImage = 0xD9;

    public static bool IsComplete(PhotoFormat format, ReadOnlySpan<byte> content) => format switch
    {
        PhotoFormat.Jpeg => JpegEndsAfterTheScan(content),
        PhotoFormat.Png => PngHasEndChunk(content),
        PhotoFormat.WebP => WebPHoldsDeclaredSize(content),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato sem regra de completude."),
    };

    /// <summary>
    /// No dado comprimido do JPEG todo <c>FF</c> vem seguido de <c>00</c>, então um <c>FF D9</c> depois do primeiro
    /// <c>FF DA</c> é o fim da imagem principal (o da miniatura do EXIF vem antes do scan).
    /// </summary>
    private static bool JpegEndsAfterTheScan(ReadOnlySpan<byte> content)
    {
        var scan = content.IndexOf([Marker, StartOfScan]);
        return scan >= 0 && content[(scan + 2)..].IndexOf([Marker, EndOfImage]) >= 0;
    }

    private static bool PngHasEndChunk(ReadOnlySpan<byte> content)
    {
        var position = (long)PngSignatureLength;
        while (position + PngChunkOverhead <= content.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(content[(int)position..]);
            var end = position + PngChunkOverhead + length;
            if (end > content.Length)
            {
                return false;
            }

            if (content.Slice((int)position + 4, 4).SequenceEqual("IEND"u8))
            {
                return true;
            }

            position = end;
        }

        return false;
    }

    private static bool WebPHoldsDeclaredSize(ReadOnlySpan<byte> content) =>
        content.Length >= RiffHeaderLength
        && RiffHeaderLength + (long)BinaryPrimitives.ReadUInt32LittleEndian(content[4..]) <= content.Length;
}
