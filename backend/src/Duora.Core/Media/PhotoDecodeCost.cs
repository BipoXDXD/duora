using System.Buffers.Binary;

namespace Duora.Core.Media;

/// <summary>
/// Teto do trabalho que um arquivo pode pedir ao decoder fora do que os pixels já limitam (spec 16.1, TM-43). O teto
/// de 20 MP e o alocador limitam pixels; dois formatos têm custo que cresce com a estrutura, não com os pixels, e
/// passam por aqui antes do decode:
/// <list type="bullet">
/// <item>JPEG progressivo: cada scan percorre os blocos da imagem inteira, e um scan quase vazio custa poucos bytes.</item>
/// <item>PNG: <c>zTXt</c>, <c>iTXt</c> e <c>iCCP</c> são inflados pelo decoder (até 8 MB cada, padrão do ImageSharp)
/// para memória fora do alocador limitado.</item>
/// </list>
/// Passar do teto é <see cref="PhotoRejection.ProcessingLimit"/>: a foto não é corrupta, só cara demais.
/// </summary>
public static class PhotoDecodeCost
{
    /// <summary>
    /// Encoders comuns geram até 10 scans num JPEG progressivo colorido (jpegtran e ImageMagick, conferido em
    /// 2026-10-19); 100 deixa folga de 10 vezes. Medido no ImageSharp 4.1.3: ~0,6 ms por scan numa imagem de 20 MP.
    /// </summary>
    public const int MaxJpegScans = 100;

    /// <summary>
    /// Um <c>iCCP</c> (o PNG admite um só), mais os perfis brutos que o ImageMagick grava como <c>zTXt</c> (EXIF, XMP e
    /// IPTC). Com o limite do decoder de 8 MB por chunk, o pior caso fica em 32 MB inflados.
    /// </summary>
    public const int MaxPngInflatedChunks = 4;

    private const int PngSignatureLength = 8;
    private const int PngChunkOverhead = 12;

    public static bool IsWithinLimits(PhotoFormat format, ReadOnlySpan<byte> content) => format switch
    {
        PhotoFormat.Jpeg => PhotoCompleteness.WalkJpeg(content).Scans <= MaxJpegScans,
        PhotoFormat.Png => InflatedPngChunks(content) <= MaxPngInflatedChunks,
        PhotoFormat.WebP => true,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato sem teto de custo."),
    };

    /// <summary>Conta os chunks que o decoder infla; para no primeiro chunk que não cabe no arquivo.</summary>
    private static int InflatedPngChunks(ReadOnlySpan<byte> content)
    {
        var inflated = 0;
        var position = (long)PngSignatureLength;
        while (position + PngChunkOverhead <= content.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(content[(int)position..]);
            var end = position + PngChunkOverhead + length;
            if (end > content.Length)
            {
                break;
            }

            if (IsInflated(content.Slice((int)position + 4, 4)))
            {
                inflated++;
            }

            position = end;
        }

        return inflated;
    }

    private static bool IsInflated(ReadOnlySpan<byte> type) =>
        type.SequenceEqual("zTXt"u8) || type.SequenceEqual("iTXt"u8) || type.SequenceEqual("iCCP"u8);
}
