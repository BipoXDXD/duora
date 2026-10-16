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
