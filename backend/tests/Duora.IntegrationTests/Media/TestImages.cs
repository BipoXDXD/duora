using System.Buffers.Binary;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Imagens sintéticas geradas no próprio teste (T-16): nunca fotos reais. As "fotos" são blocos de cor, para os
/// testes conferirem orientação pelo pixel.
/// </summary>
internal static class TestImages
{
    public static readonly Rgba32 Red = new(255, 0, 0, 255);
    public static readonly Rgba32 Blue = new(0, 0, 255, 255);

    /// <summary>Metade esquerda vermelha, direita azul.</summary>
    public static Image<Rgba32> SplitImage(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = x < width / 2 ? Red : Blue;
                }
            }
        });
        return image;
    }

    public static byte[] Jpeg(int width = 64, int height = 32) => Encode(SplitImage(width, height), new JpegEncoder { Quality = 95 });

    public static byte[] Png(int width = 64, int height = 32) => Encode(SplitImage(width, height), new PngEncoder());

    public static byte[] WebP(int width = 64, int height = 32) => Encode(SplitImage(width, height), new WebpEncoder());

    /// <summary>
    /// JPEG com EXIF de GPS sintético (um ponto no mar, longe de qualquer pessoa) e orientação 6 (girar 90° no sentido
    /// horário para exibir), como sai de um celular segurado de pé.
    /// </summary>
    public static byte[] JpegWithGpsAndRotation()
    {
        using var image = SplitImage(64, 32);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.GPSLatitudeRef, "S");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(23, 1), new Rational(33, 1), new Rational(1, 1)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "W");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(30, 1), new Rational(0, 1), new Rational(0, 1)]);
        exif.SetValue(ExifTag.Make, "CanaryCamera");
        image.Metadata.ExifProfile = exif;
        return Encode(image, new JpegEncoder { Quality = 95 });
    }

    public static byte[] AnimatedPng() => Encode(TwoFrames(), new PngEncoder());

    public static byte[] AnimatedWebP() => Encode(TwoFrames(), new WebpEncoder());

    /// <summary>
    /// PNG que declara 30000×30000 (900 megapixels, 3,6 GB em RGBA) com só alguns bytes de dados: a bomba tem de ser
    /// recusada pelo header, antes de qualquer alocação de pixels. CRCs válidos, para o arquivo não cair como corrompido.
    /// </summary>
    public static byte[] DecompressionBombPng()
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, 30_000);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 30_000);
        ihdr[8] = 8;
        ihdr[9] = 6;
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(stream, "IHDR", ihdr);
        // zlib de um bloco vazio: o decoder nunca deveria chegar aqui.
        WritePngChunk(stream, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        WritePngChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    /// <summary>JPEG válido seguido de HTML com script: o sniff aceita, o reencode descarta o resto.</summary>
    public static byte[] JpegHtmlPolyglot() =>
        [.. Jpeg(), .. Encoding.ASCII.GetBytes("<html><script>alert('duora-canary')</script></html>")];

    public static byte[] Truncated(byte[] image) => image[..(image.Length / 2)];

    public static byte[] Text(string content) => Encoding.UTF8.GetBytes(content);

    private static Image<Rgba32> TwoFrames()
    {
        var image = SplitImage(32, 32);
        using var second = new Image<Rgba32>(32, 32, Blue);
        image.Frames.AddFrame(second.Frames.RootFrame);
        return image;
    }

    private static byte[] Encode(Image image, SixLabors.ImageSharp.Formats.IImageEncoder encoder)
    {
        using (image)
        {
            using var stream = new MemoryStream();
            image.Save(stream, encoder);
            return stream.ToArray();
        }
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        stream.Write(header);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typeAndData);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
