using System.Buffers.Binary;
using System.Text;

namespace Duora.UnitTests.Media;

/// <summary>
/// Montagem de cabeçalhos de imagem sintéticos byte a byte, só com o que o sniff e a detecção de animação leem.
/// Não são imagens decodificáveis: os testes de decode ficam na integração, com imagens geradas pelo encoder.
/// </summary>
internal static class ImageBytes
{
    public static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01];

    public const byte Marker = 0xFF;
    public const byte App1 = 0xE1;
    public const byte Comment = 0xFE;
    public const byte DefineHuffmanTable = 0xC4;

    public static readonly byte[] EndOfImage = [Marker, 0xD9];

    /// <summary>JPEG estrutural: SOI seguido das partes, sem pixels decodificáveis.</summary>
    public static byte[] JpegFile(params byte[][] parts) => [0xFF, 0xD8, .. parts.SelectMany(part => part)];

    /// <summary>Segmento com comprimento declarado correto (os dois bytes do comprimento contam).</summary>
    public static byte[] Segment(byte marker, byte[] payload)
    {
        var length = (ushort)(payload.Length + 2);
        return [Marker, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    /// <summary>APP0 JFIF e um SOF0 de 1×1, o que vem antes do scan num JPEG comum.</summary>
    public static byte[] Header() =>
    [
        .. Segment(0xE0, [0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]),
        .. Segment(0xC0, [0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00]),
    ];

    /// <summary>Cabeçalho de SOS e um pedaço de dado comprimido, sem o EOI.</summary>
    public static byte[] Scan() => [.. Segment(0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]), 0x11, 0x22];

    /// <summary>Miniatura completa, como a que o EXIF guarda: SOI, scan e EOI próprios.</summary>
    public static byte[] Thumbnail() => JpegFile(Header(), Scan(), EndOfImage);

    public static byte[] Png(params (string Type, byte[] Data)[] chunks)
    {
        using var stream = new MemoryStream();
        stream.Write(PngSignature);
        foreach (var (type, data) in chunks)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            stream.Write(length);
            var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            stream.Write(typeAndData);
            // CRC zerado: a detecção não confere CRC (quem confere é o decoder, que recusa o arquivo).
            stream.Write(new byte[4]);
        }

        return stream.ToArray();
    }

    public static (string, byte[]) Ihdr(int width = 1, int height = 1)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data, width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), height);
        data[8] = 8;
        data[9] = 6;
        return ("IHDR", data);
    }

    public static (string, byte[]) Chunk(string type, int length = 0) => (type, new byte[length]);

    public static byte[] WebP(params (string FourCc, byte[] Data)[] chunks)
    {
        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        foreach (var (fourCc, data) in chunks)
        {
            body.Write(Encoding.ASCII.GetBytes(fourCc));
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
            body.Write(size);
            body.Write(data);
            if (data.Length % 2 == 1)
            {
                body.WriteByte(0);
            }
        }

        using var file = new MemoryStream();
        file.Write("RIFF"u8);
        Span<byte> riffSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(riffSize, (uint)body.Length);
        file.Write(riffSize);
        file.Write(body.ToArray());
        return file.ToArray();
    }

    /// <summary>Chunk VP8X com as flags pedidas (bit 1 = animação) e canvas 1×1.</summary>
    public static (string, byte[]) Vp8x(byte flags)
    {
        var data = new byte[10];
        data[0] = flags;
        return ("VP8X", data);
    }
}
