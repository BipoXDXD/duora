using System.Buffers.Binary;

namespace Duora.Core.Media;

/// <summary>
/// Detecta animação pela estrutura do arquivo, sem decodificar (TM-43): APNG declara <c>acTL</c> e WebP animado
/// declara a flag no <c>VP8X</c> ou traz <c>ANIM</c>/<c>ANMF</c>. É uma segunda checagem, independente do decoder,
/// para uma animação não passar como a primeira imagem dela. Arquivo malformado não é declarado animado aqui: quem
/// o recusa é o decoder.
/// </summary>
public static class PhotoAnimation
{
    private const int PngSignatureLength = 8;
    private const int PngChunkOverhead = 12;
    private const int RiffHeaderLength = 12;
    private const int RiffChunkHeaderLength = 8;
    private const byte Vp8xAnimationFlag = 0x02;

    public static bool IsAnimated(PhotoFormat format, ReadOnlySpan<byte> content) => format switch
    {
        PhotoFormat.Jpeg => false,
        PhotoFormat.Png => PngHasAnimationControl(content),
        PhotoFormat.WebP => WebPIsAnimated(content),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato sem regra de animação."),
    };

    /// <summary>Percorre todos os chunks: um <c>acTL</c> fora da ordem também conta.</summary>
    private static bool PngHasAnimationControl(ReadOnlySpan<byte> content)
    {
        var position = (long)PngSignatureLength;
        while (position + PngChunkOverhead <= content.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(content[(int)position..]);
            var type = content.Slice((int)position + 4, 4);
            if (type.SequenceEqual("acTL"u8))
            {
                return true;
            }

            position += PngChunkOverhead + (long)length;
        }

        return false;
    }

    private static bool WebPIsAnimated(ReadOnlySpan<byte> content)
    {
        var position = (long)RiffHeaderLength;
        while (position + RiffChunkHeaderLength <= content.Length)
        {
            var fourCc = content.Slice((int)position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(content[((int)position + 4)..]);
            var dataStart = position + RiffChunkHeaderLength;
            if (IsAnimationChunk(fourCc) || (fourCc.SequenceEqual("VP8X"u8) && HasAnimationFlag(content, dataStart)))
            {
                return true;
            }

            // Chunks RIFF de tamanho ímpar levam um byte de preenchimento.
            position = dataStart + size + (size % 2);
        }

        return false;
    }

    private static bool IsAnimationChunk(ReadOnlySpan<byte> fourCc) =>
        fourCc.SequenceEqual("ANIM"u8) || fourCc.SequenceEqual("ANMF"u8);

    private static bool HasAnimationFlag(ReadOnlySpan<byte> content, long dataStart) =>
        dataStart < content.Length && (content[(int)dataStart] & Vp8xAnimationFlag) != 0;
}
