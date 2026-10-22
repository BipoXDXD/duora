using Duora.Core.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Duora.IntegrationTests.Media;

internal sealed record SanitizerBaselineCase(string Name, PhotoFormat Format, Func<byte[]> Create,
    string? RejectionCode = null, int Width = 64, int Height = 32);

internal static class SanitizerBaselineCorpus
{
    public const uint Seed = 0x1742026;

    public static IReadOnlyList<SanitizerBaselineCase> Cases { get; } =
    [
        new("jpeg-20mp-texture", PhotoFormat.Jpeg, () => Texture(PhotoFormat.Jpeg, 5000, 4000, 8), Width: 5000, Height: 4000),
        new("png-20mp-texture", PhotoFormat.Png, () => Texture(PhotoFormat.Png, 5000, 4000, 8), Width: 5000, Height: 4000),
        new("webp-20mp-texture", PhotoFormat.WebP, () => Texture(PhotoFormat.WebP, 5000, 4000, 8), Width: 5000, Height: 4000),
        new("png-dense-noise", PhotoFormat.Png, () => Texture(PhotoFormat.Png, 1280, 1280, 1), Width: 1280, Height: 1280),
        new("jpeg-metadata-icc", PhotoFormat.Jpeg, () => TestImages.WithEveryMetadata(PhotoFormat.Jpeg)),
        new("png-metadata-icc", PhotoFormat.Png, () => TestImages.WithEveryMetadata(PhotoFormat.Png)),
        new("webp-metadata-icc", PhotoFormat.WebP, () => TestImages.WithEveryMetadata(PhotoFormat.WebP)),
        new("jpeg-gps-rotation", PhotoFormat.Jpeg, TestImages.JpegWithGpsAndRotation, Width: 32, Height: 64),
        new("png-html-polyglot", PhotoFormat.Png, TestImages.PngHtmlPolyglot),
        new("jpeg-zip-polyglot", PhotoFormat.Jpeg, TestImages.JpegZipPolyglot),
        new("png-animation", PhotoFormat.Png, TestImages.AnimatedPng, "ANIMATED"),
        new("webp-animation", PhotoFormat.WebP, TestImages.AnimatedWebP, "ANIMATED"),
        new("png-declared-jpeg", PhotoFormat.Jpeg, () => TestImages.Png(), "TYPE_MISMATCH"),
        new("jpeg-bomb", PhotoFormat.Jpeg, TestImages.DecompressionBombJpeg, "TOO_MANY_PIXELS"),
        new("png-bomb", PhotoFormat.Png, TestImages.DecompressionBombPng, "TOO_MANY_PIXELS"),
        new("webp-bomb", PhotoFormat.WebP, TestImages.DecompressionBombWebP, "TOO_MANY_PIXELS"),
        new("png-truncated", PhotoFormat.Png, () => TestImages.Truncated(TestImages.Png()), "CORRUPT_IMAGE"),
        new("jpeg-byte-limit", PhotoFormat.Jpeg, () => new byte[PhotoLimits.MaxBytes + 1], "PROCESSING_LIMIT"),
    ];

    private static byte[] Texture(PhotoFormat format, int width, int height, int block)
    {
        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var value = unchecked(Seed ^ ((uint)(x / block) * 0x9E3779B9u) ^ ((uint)(y / block) * 0x85EBCA6Bu));
                    value ^= value << 13;
                    value ^= value >> 17;
                    value ^= value << 5;
                    row[x] = new Rgb24((byte)value, (byte)(value >> 8), (byte)(value >> 16));
                }
            }
        });
        IImageEncoder encoder = format switch
        {
            PhotoFormat.Jpeg => new JpegEncoder { Quality = 85 },
            PhotoFormat.Png => new PngEncoder(),
            PhotoFormat.WebP => new WebpEncoder { FileFormat = WebpFileFormatType.Lossless },
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        using var output = new MemoryStream();
        image.Save(output, encoder);
        return output.ToArray();
    }
}
