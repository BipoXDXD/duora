extern alias worker;

using System.Text;
using Duora.Core.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using worker::Duora.Worker.Media;

namespace Duora.IntegrationTests.Media;

/// <summary>Sanitização do ImageSharp no Worker (ADR-0025, T-16), com imagens geradas no teste.</summary>
[Trait("Acceptance", "T-16")]
public sealed class PhotoSanitizerTests
{
    private readonly ImageSharpPhotoSanitizer _sanitizer = new(new PhotoSanitizerOptions());

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task AStaticImageBecomesThreeWebPVariants(string format)
    {
        var (declared, bytes) = format switch
        {
            "jpeg" => (PhotoFormat.Jpeg, TestImages.Jpeg(4000, 2000)),
            "png" => (PhotoFormat.Png, TestImages.Png(4000, 2000)),
            _ => (PhotoFormat.WebP, TestImages.WebP(4000, 2000)),
        };

        var sanitized = Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizeAsync(declared, bytes));

        Assert.Equal(declared, sanitized.Detected);
        Assert.Equal((4000, 2000), (sanitized.Width, sanitized.Height));
        Assert.Equal(
            [(PhotoVariant.Thumb, 256, 128), (PhotoVariant.Card, 768, 384), (PhotoVariant.Full, 1600, 800)],
            sanitized.Variants.Select(v => (v.Kind, v.Width, v.Height)));
        foreach (var variant in sanitized.Variants)
        {
            Assert.Equal(PhotoFormat.WebP, PhotoFormats.Sniff(variant.Content));
            var info = Image.Identify(variant.Content);
            Assert.Equal((variant.Width, variant.Height), (info.Width, info.Height));
        }
    }

    [Fact]
    public async Task ASmallImageIsNotEnlarged()
    {
        var sanitized = Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizeAsync(PhotoFormat.Png, TestImages.Png(100, 50)));

        Assert.All(sanitized.Variants, variant => Assert.Equal((100, 50), (variant.Width, variant.Height)));
    }

    [Fact]
    public async Task GpsAndOtherMetadataAreRemovedAndTheOrientationIsApplied()
    {
        var original = TestImages.JpegWithGpsAndRotation();
        Assert.NotNull(Image.Identify(original).Metadata.ExifProfile);

        var sanitized = Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizeAsync(PhotoFormat.Jpeg, original));

        // 64×32 com orientação 6 exibe 32×64, e a metade vermelha da esquerda passa para cima.
        Assert.Equal((32, 64), (sanitized.Width, sanitized.Height));
        foreach (var variant in sanitized.Variants)
        {
            using var image = Image.Load<Rgba32>(variant.Content);
            Assert.Null(image.Metadata.ExifProfile);
            Assert.Null(image.Metadata.XmpProfile);
            Assert.Null(image.Metadata.IptcProfile);
            Assert.Null(image.Metadata.IccProfile);
            Assert.Null(image.Frames.RootFrame.Metadata.ExifProfile);
            Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(variant.Content), StringComparison.Ordinal);
            Assert.DoesNotContain("CanaryCamera", Encoding.Latin1.GetString(variant.Content), StringComparison.Ordinal);
            Assert.True(IsReddish(image[image.Width / 2, image.Height / 4]), "A metade de cima deveria ser vermelha.");
            Assert.False(IsReddish(image[image.Width / 2, image.Height * 3 / 4]), "A metade de baixo deveria ser azul.");
        }
    }

    public static TheoryData<string, PhotoFormat, string> NotImages => new()
    {
        { "svg", PhotoFormat.Png, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>" },
        { "html", PhotoFormat.Jpeg, "<!DOCTYPE html><html><body><script>alert(1)</script></body></html>" },
        { "pe", PhotoFormat.WebP, "MZ\u0090\u0000\u0003\u0000\u0000\u0000\u0004\u0000PE" },
        { "elf", PhotoFormat.Png, "\u007FELF\u0002\u0001\u0001\u0000" },
    };

    [Theory]
    [MemberData(nameof(NotImages))]
    public async Task SvgHtmlAndExecutablesRenamedAsImagesAreUnsupported(string label, PhotoFormat declared, string content)
    {
        var outcome = await SanitizeAsync(declared, TestImages.Text(content));

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.UnsupportedFormat), outcome);
        Assert.NotEmpty(label);
    }

    [Theory]
    [InlineData(PhotoFormat.Jpeg)]
    [InlineData(PhotoFormat.WebP)]
    public async Task APngDeclaredAsAnotherTypeIsAMismatch(PhotoFormat declared)
    {
        Assert.Equal(
            new SanitizeOutcome.Rejected(PhotoRejection.TypeMismatch),
            await SanitizeAsync(declared, TestImages.Png()));
    }

    [Fact]
    public async Task APolyglotIsReencodedAndLosesTheTrailingScript()
    {
        var sanitized = Assert.IsType<SanitizeOutcome.Sanitized>(
            await SanitizeAsync(PhotoFormat.Jpeg, TestImages.JpegHtmlPolyglot()));

        Assert.All(sanitized.Variants, variant =>
            Assert.DoesNotContain("duora-canary", Encoding.Latin1.GetString(variant.Content), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADecompressionBombIsRefusedFromTheHeader()
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);

        var outcome = await SanitizeAsync(PhotoFormat.Png, TestImages.DecompressionBombPng());

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.TooManyPixels), outcome);
        // 900 megapixels em RGBA seriam 3,6 GB; recusar pelo header aloca uma fração mínima disso.
        Assert.True(GC.GetTotalAllocatedBytes(precise: true) - before < 64 * 1024 * 1024);
    }

    [Fact]
    public async Task AnAnimatedPngIsRefused()
    {
        var apng = TestImages.AnimatedPng();
        Assert.Contains("acTL", Encoding.Latin1.GetString(apng), StringComparison.Ordinal);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.Animated), await SanitizeAsync(PhotoFormat.Png, apng));
    }

    [Fact]
    public async Task AnAnimatedWebPIsRefused()
    {
        var webp = TestImages.AnimatedWebP();
        Assert.Contains("ANIM", Encoding.Latin1.GetString(webp), StringComparison.Ordinal);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.Animated), await SanitizeAsync(PhotoFormat.WebP, webp));
    }

    [Theory]
    [InlineData(PhotoFormat.Jpeg)]
    [InlineData(PhotoFormat.Png)]
    [InlineData(PhotoFormat.WebP)]
    public async Task ATruncatedFileIsACorruptImage(PhotoFormat format)
    {
        var complete = format switch
        {
            PhotoFormat.Jpeg => TestImages.Jpeg(400, 300),
            PhotoFormat.Png => TestImages.Png(400, 300),
            _ => TestImages.WebP(400, 300),
        };

        Assert.Equal(
            new SanitizeOutcome.Rejected(PhotoRejection.CorruptImage),
            await SanitizeAsync(format, TestImages.Truncated(complete)));
    }

    [Fact]
    public async Task AnImageOverTheMemoryLimitIsAProcessingLimit()
    {
        var tight = new ImageSharpPhotoSanitizer(new PhotoSanitizerOptions { MemoryLimitMegabytes = 1 });

        var outcome = await tight.SanitizeAsync(PhotoFormat.Png, TestImages.Png(2000, 2000), TestContext.Current.CancellationToken);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit), outcome);
    }

    [Fact]
    public async Task AnImageOverTheTimeLimitIsAProcessingLimit()
    {
        var hurried = new ImageSharpPhotoSanitizer(new PhotoSanitizerOptions { Timeout = TimeSpan.FromTicks(1) });

        var outcome = await hurried.SanitizeAsync(PhotoFormat.Png, TestImages.Png(3000, 3000), TestContext.Current.CancellationToken);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit), outcome);
    }

    private static bool IsReddish(Rgba32 pixel) => pixel.R > 200 && pixel.B < 80;

    private Task<SanitizeOutcome> SanitizeAsync(PhotoFormat declared, byte[] bytes) =>
        _sanitizer.SanitizeAsync(declared, bytes, TestContext.Current.CancellationToken);
}
