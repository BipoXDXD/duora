using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Duora.Core.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.Metadata.Profiles.Iptc;
using SixLabors.ImageSharp.Metadata.Profiles.Xmp;
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

    /// <summary>Textos sintéticos semeados em cada metadado; nenhum pode sobrar num derivado.</summary>
    public const string ExifCanary = "duora-exif-canary";
    public const string XmpCanary = "duora-xmp-canary";
    public const string IptcCanary = "duora-iptc-canary";
    public const string IccCanary = "duora-icc-canary";
    public const string PngTextCanary = "duora-png-text-canary";
    public const string ZipCanary = "duora-zip-canary";

    private const int JpegSegmentHeaderLength = 4;

    private static readonly string[] ToneCurves = ["rTRC", "gTRC", "bTRC"];

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

    /// <summary>
    /// Imagem dividida com EXIF, XMP, IPTC (só o JPEG grava), texto PNG (só o PNG) e um ICC que troca os primários
    /// vermelho e verde: o pixel "vermelho" do arquivo é o verde do sRGB, e só a conversão do perfil o mostra verde.
    /// </summary>
    public static byte[] WithEveryMetadata(PhotoFormat format)
    {
        var image = SplitImage(64, 32);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, ExifCanary);
        exif.SetValue(ExifTag.GPSLatitudeRef, "S");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(23, 1), new Rational(33, 1), new Rational(1, 1)]);
        image.Metadata.ExifProfile = exif;
        image.Metadata.XmpProfile = new XmpProfile(Encoding.UTF8.GetBytes(Xmp(XmpCanary)));
        var iptc = new IptcProfile();
        iptc.SetValue(IptcTag.Caption, IptcCanary);
        image.Metadata.IptcProfile = iptc;
        image.Metadata.IccProfile = new IccProfile(SwappedPrimariesIcc());
        image.Metadata.GetPngMetadata().TextData.Add(new PngTextData("Comment", PngTextCanary, "pt", "Comentario"));
        return Encode(image, format switch
        {
            PhotoFormat.Jpeg => new JpegEncoder { Quality = 95 },
            PhotoFormat.Png => new PngEncoder(),
            PhotoFormat.WebP => new WebpEncoder { FileFormat = WebpFileFormatType.Lossless },
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato sem encoder."),
        });
    }

    /// <summary>JPEG com miniatura de verdade no EXIF (IFD1 aponta para um JPEG inteiro dentro do APP1), como sai do celular.</summary>
    public static byte[] JpegWithExifThumbnail(int width = 400, int height = 300) =>
        InsertAfterStartOfImage(Jpeg(width, height), JpegSegment(0xE1, [.. "Exif\0\0"u8, .. TiffWithThumbnail(Jpeg(16, 16))]));

    /// <summary>JPEG com outro JPEG inteiro dentro de um segmento COM.</summary>
    public static byte[] JpegWithThumbnailInAComment(int width = 400, int height = 300) =>
        InsertAfterStartOfImage(Jpeg(width, height), JpegSegment(0xFE, Jpeg(16, 16)));

    /// <summary>JPEG cujo SOF0 declara 65500×65500 (4,3 gigapixels) sobre os dados de uma imagem pequena.</summary>
    public static byte[] DecompressionBombJpeg()
    {
        var jpeg = Jpeg();
        var sof = jpeg.AsSpan().IndexOf([(byte)0xFF, (byte)0xC0]);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 5), 65_500);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 7), 65_500);
        return jpeg;
    }

    /// <summary>WebP lossy cujo quadro VP8 declara 16383×16383 (268 megapixels, o máximo do formato).</summary>
    public static byte[] DecompressionBombWebP()
    {
        var webp = Encode(SplitImage(64, 32), new WebpEncoder { FileFormat = WebpFileFormatType.Lossy });
        var frame = webp.AsSpan().IndexOf("VP8 "u8) + 8;
        const ushort MaxSide = 16_383;
        BinaryPrimitives.WriteUInt16LittleEndian(webp.AsSpan(frame + 6), MaxSide);
        BinaryPrimitives.WriteUInt16LittleEndian(webp.AsSpan(frame + 8), MaxSide);
        return webp;
    }

    /// <summary>PNG válido seguido de HTML com script, depois do IEND.</summary>
    public static byte[] PngHtmlPolyglot() =>
        [.. Png(), .. Encoding.ASCII.GetBytes("<html><script>alert('duora-canary')</script></html>")];

    /// <summary>JPEG seguido de um ZIP de verdade: abre como imagem e como arquivo compactado.</summary>
    public static byte[] JpegZipPolyglot() => [.. Jpeg(), .. Zip()];

    /// <summary>Chunks RIFF de um WebP, na ordem do arquivo.</summary>
    public static IReadOnlyList<(string FourCc, byte[] Data)> RiffChunks(byte[] webp)
    {
        var chunks = new List<(string, byte[])>();
        var position = 12;
        while (position + 8 <= webp.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(position + 4));
            chunks.Add((Encoding.ASCII.GetString(webp, position, 4), webp[(position + 8)..(position + 8 + size)]));
            position += 8 + size + (size % 2);
        }

        return chunks;
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

    /// <summary>
    /// PNG estruturalmente completo (IHDR, IDAT e IEND com CRCs válidos) cujo IDAT tem um cabeçalho zlib correto e um
    /// stream deflate de lixo determinístico: a completude passa e só o decoder percebe a corrupção.
    /// </summary>
    public static byte[] PngWithGarbageCompressedData()
    {
        byte[] garbage = [0x78, 0x9C, .. Enumerable.Range(0, 64).Select(i => (byte)((i * 37) + 0xF7))];
        return Png16(garbage);
    }

    private static readonly byte[] DefineHuffmanTable = [0xFF, 0xC4];

    /// <summary>PNG válido com o CRC do IDAT invertido: estrutura completa, dado que não confere.</summary>
    public static byte[] PngWithWrongCrc()
    {
        var png = Png(64, 32);
        var type = png.AsSpan().IndexOf("IDAT"u8);
        var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(type - 4));
        png[type + 4 + length] ^= 0xFF;
        return png;
    }

    /// <summary>
    /// JPEG válido com a primeira DHT adulterada: 255 códigos de 1 bit, uma tabela de Huffman impossível. O resto do
    /// arquivo (SOS, scan e EOI) fica intacto, então a completude passa.
    /// </summary>
    public static byte[] JpegWithTamperedHuffmanTable()
    {
        const int FirstCodeCountOffset = 5;
        var jpeg = Jpeg(64, 32);
        var dht = jpeg.AsSpan().IndexOf(DefineHuffmanTable);
        jpeg[dht + FirstCodeCountOffset] = 0xFF;
        return jpeg;
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

    private static string Xmp(string canary) =>
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>" + canary
        + "</dc:title></rdf:Description></rdf:RDF></x:xmpmeta>";

    private static byte[] Zip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(archive.CreateEntry("leia.txt").Open());
            entry.Write(ZipCanary);
        }

        return stream.ToArray();
    }

    private static byte[] InsertAfterStartOfImage(byte[] jpeg, byte[] segment) => [.. jpeg[..2], .. segment, .. jpeg[2..]];

    private static byte[] JpegSegment(byte marker, byte[] payload)
    {
        var segment = new byte[JpegSegmentHeaderLength + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment.AsSpan(JpegSegmentHeaderLength));
        return segment;
    }

    /// <summary>TIFF little-endian: IFD0 vazio e IFD1 com JPEGInterchangeFormat/Length apontando para a miniatura.</summary>
    private static byte[] TiffWithThumbnail(byte[] thumbnail)
    {
        const int Ifd1 = 14;
        const int ThumbnailOffset = Ifd1 + 2 + (2 * 12) + 4;
        var tiff = new byte[ThumbnailOffset + thumbnail.Length];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(10), Ifd1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(Ifd1), 2);
        WriteIfdLong(tiff.AsSpan(Ifd1 + 2), 0x0201, ThumbnailOffset);
        WriteIfdLong(tiff.AsSpan(Ifd1 + 14), 0x0202, (uint)thumbnail.Length);
        thumbnail.CopyTo(tiff.AsSpan(ThumbnailOffset));
        return tiff;
    }

    private static void WriteIfdLong(Span<byte> entry, ushort tag, uint value)
    {
        const ushort LongType = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(entry, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], LongType);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], value);
    }

    /// <summary>
    /// Perfil ICC v2 de monitor RGB com os primários vermelho e verde do sRGB trocados e curva identidade. Colorantes
    /// já adaptados a D50, como o ICC exige.
    /// </summary>
    private static byte[] SwappedPrimariesIcc()
    {
        var description = IccTextDescription(IccCanary);
        var xyz = new Dictionary<string, (double X, double Y, double Z)>
        {
            ["wtpt"] = (0.9642, 1.0, 0.8249),
            ["rXYZ"] = (0.3851, 0.7169, 0.0971),
            ["gXYZ"] = (0.4361, 0.2225, 0.0139),
            ["bXYZ"] = (0.1431, 0.0606, 0.7141),
        };
        byte[] identityCurve = [.. "curv"u8, 0, 0, 0, 0, 0, 0, 0, 0];
        var tags = new List<(string Signature, byte[] Data)> { ("desc", description) };
        tags.AddRange(xyz.Select(pair => (pair.Key, IccXyz(pair.Value))));
        tags.AddRange(ToneCurves.Select(signature => (signature, identityCurve)));

        const int HeaderLength = 128;
        var tableLength = 4 + (12 * tags.Count);
        using var body = new MemoryStream();
        var entries = new List<(string, int, int)>();
        foreach (var (signature, data) in tags)
        {
            entries.Add((signature, HeaderLength + tableLength + (int)body.Length, data.Length));
            body.Write(data);
            while (body.Length % 4 != 0)
            {
                body.WriteByte(0);
            }
        }

        var profile = new byte[HeaderLength + tableLength + body.Length];
        var span = profile.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x02100000);
        "mntrRGB XYZ "u8.CopyTo(span[12..]);
        "acsp"u8.CopyTo(span[36..]);
        WriteS15Fixed16(span[68..], xyz["wtpt"]);
        BinaryPrimitives.WriteUInt32BigEndian(span[HeaderLength..], (uint)tags.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var (signature, offset, size) = entries[i];
            var entry = span[(HeaderLength + 4 + (12 * i))..];
            Encoding.ASCII.GetBytes(signature).CopyTo(entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)size);
        }

        body.ToArray().CopyTo(span[(HeaderLength + tableLength)..]);
        return profile;
    }

    private static byte[] IccXyz((double X, double Y, double Z) value)
    {
        var data = new byte[20];
        "XYZ "u8.CopyTo(data);
        WriteS15Fixed16(data.AsSpan(8), value);
        return data;
    }

    private static void WriteS15Fixed16(Span<byte> target, (double X, double Y, double Z) value)
    {
        const double One = 65536;
        BinaryPrimitives.WriteInt32BigEndian(target, (int)Math.Round(value.X * One));
        BinaryPrimitives.WriteInt32BigEndian(target[4..], (int)Math.Round(value.Y * One));
        BinaryPrimitives.WriteInt32BigEndian(target[8..], (int)Math.Round(value.Z * One));
    }

    /// <summary>textDescriptionType do ICC v2: ASCII terminado em zero, Unicode vazio e ScriptCode vazio (67 bytes).</summary>
    private static byte[] IccTextDescription(string text)
    {
        const int ScriptCodeLength = 67;
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var data = new byte[12 + ascii.Length + 8 + 3 + ScriptCodeLength];
        "desc"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)ascii.Length);
        ascii.CopyTo(data.AsSpan(12));
        return data;
    }

    /// <summary>PNG 16×16 RGBA de 8 bits com o IDAT dado e CRCs válidos.</summary>
    private static byte[] Png16(byte[] idat)
    {
        const int Side = 16;
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, Side);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), Side);
        ihdr[8] = 8;
        ihdr[9] = 6;
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(stream, "IHDR", ihdr);
        WritePngChunk(stream, "IDAT", idat);
        WritePngChunk(stream, "IEND", []);
        return stream.ToArray();
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
