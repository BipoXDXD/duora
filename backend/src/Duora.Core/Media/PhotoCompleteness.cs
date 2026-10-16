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
    private const int JpegStartOfImageLength = 2;
    private const byte Marker = 0xFF;
    private const byte Stuffing = 0x00;
    private const byte StandaloneTemporary = 0x01;
    private const byte FirstSegmentMarker = 0xC0;
    private const byte FirstRestart = 0xD0;
    private const byte LastRestart = 0xD7;
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;
    private const byte StartOfScan = 0xDA;

    public static bool IsComplete(PhotoFormat format, ReadOnlySpan<byte> content) => format switch
    {
        PhotoFormat.Jpeg => JpegEndsAfterTheScan(content),
        PhotoFormat.Png => PngHasEndChunk(content),
        PhotoFormat.WebP => WebPHoldsDeclaredSize(content),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato sem regra de completude."),
    };

    /// <summary>
    /// Percorre os segmentos pelo comprimento declarado, pulando APPn e COM inteiros (a miniatura do EXIF é um JPEG
    /// completo dentro do APP1, com SOS e EOI próprios), e exige o EOI depois do dado comprimido do último scan.
    /// Comprimento que passa do arquivo, ou que não cai num marcador, é arquivo incompleto.
    /// </summary>
    private static bool JpegEndsAfterTheScan(ReadOnlySpan<byte> content)
    {
        var position = JpegStartOfImageLength;
        var scanned = false;
        while (position + 1 < content.Length && content[position] == Marker)
        {
            var marker = content[position + 1];
            if (marker == Marker)
            {
                position++;
                continue;
            }

            position += 2;
            if (marker == EndOfImage)
            {
                return scanned;
            }

            if (marker == StandaloneTemporary || IsRestart(marker))
            {
                continue;
            }

            if (!TrySkipSegment(marker, content, ref position))
            {
                return false;
            }

            if (marker == StartOfScan)
            {
                scanned = true;
                position = NextMarkerAfterEntropyCodedData(content, position);
            }
        }

        return false;
    }

    /// <summary>Marcador com comprimento: avança até o fim do segmento, se ele couber no arquivo.</summary>
    private static bool TrySkipSegment(byte marker, ReadOnlySpan<byte> content, ref int position)
    {
        if (marker < FirstSegmentMarker || marker == StartOfImage || position + 2 > content.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(content[position..]);
        if (length < 2 || position + length > content.Length)
        {
            return false;
        }

        position += length;
        return true;
    }

    /// <summary>
    /// No dado comprimido, <c>FF 00</c> é um <c>FF</c> literal, <c>FF D0</c>–<c>FF D7</c> são restarts e <c>FF FF</c>
    /// é preenchimento; o primeiro outro marcador encerra o scan. Sem ele, devolve o fim do arquivo.
    /// </summary>
    private static int NextMarkerAfterEntropyCodedData(ReadOnlySpan<byte> content, int position)
    {
        while (position < content.Length)
        {
            var next = content[position..].IndexOf(Marker);
            if (next < 0 || position + next + 1 >= content.Length)
            {
                return content.Length;
            }

            position += next;
            var code = content[position + 1];
            if (code is not (Stuffing or Marker) && !IsRestart(code))
            {
                return position;
            }

            position += code == Marker ? 1 : 2;
        }

        return content.Length;
    }

    private static bool IsRestart(byte marker) => marker is >= FirstRestart and <= LastRestart;

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
