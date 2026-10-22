extern alias sanitizer;

using System.Text;
using Duora.Core.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using sanitizer::Duora.MediaSanitizer.Media;

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

    /// <summary>O que cada fixture leva de metadado, como aparece nos bytes de entrada.</summary>
    public static TheoryData<PhotoFormat, string[]> MetadataCarriers => new()
    {
        { PhotoFormat.Jpeg, ["Exif\0\0", "http://ns.adobe.com/xap/1.0/", "Photoshop 3.0", "ICC_PROFILE", TestImages.IptcCanary] },
        { PhotoFormat.Png, ["eXIf", "iTXt", "iCCP", "XML:com.adobe.xmp", TestImages.PngTextCanary] },
        { PhotoFormat.WebP, ["VP8X", "EXIF", "XMP ", "ICCP"] },
    };

    [Theory]
    [MemberData(nameof(MetadataCarriers))]
    public async Task EveryMetadataBlockIsGoneFromTheBytesOfEachVariant(PhotoFormat format, string[] carriers)
    {
        var original = TestImages.WithEveryMetadata(format);
        var input = Encoding.Latin1.GetString(original);
        Assert.All(carriers, carrier => Assert.Contains(carrier, input, StringComparison.Ordinal));
        Assert.Contains(TestImages.ExifCanary, input, StringComparison.Ordinal);
        Assert.Contains(TestImages.XmpCanary, input, StringComparison.Ordinal);

        var sanitized = Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizeAsync(format, original));

        foreach (var variant in sanitized.Variants)
        {
            // Só o bitstream da imagem: nenhum chunk de metadado nem VP8X que anuncie um.
            Assert.All(TestImages.RiffChunks(variant.Content), chunk => Assert.Contains(chunk.FourCc, ImageOnlyChunks));
            var output = Encoding.Latin1.GetString(variant.Content);
            Assert.All(
                [TestImages.ExifCanary, TestImages.XmpCanary, TestImages.IptcCanary, TestImages.IccCanary, TestImages.PngTextCanary,
                    "Exif", "xmpmeta", "ICCP", "8BIM"],
                canary => Assert.DoesNotContain(canary, output, StringComparison.Ordinal));
            using var image = Image.Load<Rgba32>(variant.Content);
            Assert.Null(image.Metadata.ExifProfile);
            Assert.Null(image.Metadata.XmpProfile);
            Assert.Null(image.Metadata.IptcProfile);
            Assert.Null(image.Metadata.IccProfile);
        }
    }

    [Theory]
    [InlineData(PhotoFormat.Jpeg)]
    [InlineData(PhotoFormat.Png)]
    [InlineData(PhotoFormat.WebP)]
    public async Task AnIccProfileIsAppliedBeforeItIsDropped(PhotoFormat format)
    {
        // O perfil da fixture troca vermelho e verde: sem a conversão, a metade esquerda sairia vermelha.
        var sanitized = Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizeAsync(format, TestImages.WithEveryMetadata(format)));

        using var image = Image.Load<Rgba32>(sanitized.Variants.Single(v => v.Kind == PhotoVariant.Full).Content);
        var left = image[image.Width / 4, image.Height / 2];
        var right = image[image.Width * 3 / 4, image.Height / 2];
        Assert.True(left.G > 200 && left.R < 80, $"A metade esquerda deveria ser verde em sRGB, veio {left}.");
        Assert.True(right.B > 200 && right.R < 80 && right.G < 80, $"A metade direita deveria continuar azul, veio {right}.");
    }

    [Theory]
    [InlineData("exif")]
    [InlineData("comment")]
    public async Task AJpegCutInHalfIsCorruptEvenWithACompleteThumbnailInside(string carrier)
    {
        // Celular: miniatura inteira (com SOS e EOI) no APP1 antes do scan principal, e o upload cortado no meio.
        var complete = carrier == "exif" ? TestImages.JpegWithExifThumbnail() : TestImages.JpegWithThumbnailInAComment();

        Assert.Equal(
            new SanitizeOutcome.Rejected(PhotoRejection.CorruptImage),
            await SanitizeAsync(PhotoFormat.Jpeg, TestImages.Truncated(complete)));
    }

    [Theory]
    [InlineData("exif")]
    [InlineData("comment")]
    public async Task AnIntactJpegWithAThumbnailIsSanitized(string carrier)
    {
        var jpeg = carrier == "exif" ? TestImages.JpegWithExifThumbnail() : TestImages.JpegWithThumbnailInAComment();

        var sanitized = Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizeAsync(PhotoFormat.Jpeg, jpeg));

        Assert.Equal((400, 300), (sanitized.Width, sanitized.Height));
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

    public static TheoryData<string, PhotoFormat> Polyglots => new()
    {
        { "jpeg+html", PhotoFormat.Jpeg },
        { "png+html", PhotoFormat.Png },
        { "jpeg+zip", PhotoFormat.Jpeg },
    };

    [Theory]
    [MemberData(nameof(Polyglots))]
    public async Task APolyglotIsReencodedAndLosesWhatFollowsTheImage(string polyglot, PhotoFormat format)
    {
        var original = polyglot switch
        {
            "jpeg+html" => TestImages.JpegHtmlPolyglot(),
            "png+html" => TestImages.PngHtmlPolyglot(),
            _ => TestImages.JpegZipPolyglot(),
        };

        var sanitized = Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizeAsync(format, original));

        Assert.All(sanitized.Variants, variant =>
        {
            var output = Encoding.Latin1.GetString(variant.Content);
            Assert.DoesNotContain("duora-canary", output, StringComparison.Ordinal);
            Assert.DoesNotContain("PK\u0003\u0004", output, StringComparison.Ordinal);
            Assert.DoesNotContain("leia.txt", output, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(PhotoFormat.Png)]
    [InlineData(PhotoFormat.Jpeg)]
    [InlineData(PhotoFormat.WebP)]
    public async Task ADecompressionBombIsRefusedFromTheHeader(PhotoFormat format)
    {
        var bomb = format switch
        {
            PhotoFormat.Png => TestImages.DecompressionBombPng(),
            PhotoFormat.Jpeg => TestImages.DecompressionBombJpeg(),
            _ => TestImages.DecompressionBombWebP(),
        };
        // Com 2 MB de teto, qualquer tentativa de alocar os pixels viraria PROCESSING_LIMIT; TOO_MANY_PIXELS só sai
        // da checagem do header, antes do decode.
        var tight = new ImageSharpPhotoSanitizer(new PhotoSanitizerOptions { MemoryLimitMegabytes = 2 });

        var outcome = await tight.SanitizeAsync(format, bomb, TestContext.Current.CancellationToken);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.TooManyPixels), outcome);
    }

    [Fact]
    public async Task AProgressiveJpegWithTheMaximumNumberOfScansIsSanitized()
    {
        var jpeg = TestImages.ProgressiveJpegWithScans(100);

        Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizeAsync(PhotoFormat.Jpeg, jpeg));
    }

    [Fact]
    public async Task AProgressiveJpegWithTooManyScansIsAProcessingLimitBeforeTheDecode()
    {
        // O arquivo é decodificável: sem o teto, o decoder o aceitaria, gastando tempo proporcional aos scans.
        var jpeg = TestImages.ProgressiveJpegWithScans(101);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit), await SanitizeAsync(PhotoFormat.Jpeg, jpeg));
    }

    [Fact]
    public async Task APngWithTheMaximumNumberOfCompressedTextChunksIsSanitized()
    {
        var png = TestImages.PngWithCompressedText(4, inflatedBytes: 1024);

        Assert.IsType<SanitizeOutcome.Sanitized>(await SanitizeAsync(PhotoFormat.Png, png));
    }

    [Fact]
    public async Task APngWithTooManyCompressedTextChunksIsAProcessingLimitBeforeTheDecode()
    {
        // Cada zTXt infla para 1 MiB fora do alocador limitado; sem o teto, o decoder os aceitaria todos.
        var png = TestImages.PngWithCompressedText(5, inflatedBytes: 1024 * 1024);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit), await SanitizeAsync(PhotoFormat.Png, png));
    }

    [Fact]
    public async Task TheMeasuredCompressedTextAttackIsAProcessingLimit()
    {
        // O ataque medido em 2026-10-19: 640 zTXt num PNG (lá, de 8 MB cada, 3,3 GB no processo). Aqui cada um infla
        // para 64 KiB, para o teste não pôr a máquina em risco se a defesa sumir; a contagem é a mesma.
        var png = TestImages.PngWithCompressedText(640, inflatedBytes: 64 * 1024);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit), await SanitizeAsync(PhotoFormat.Png, png));
    }

    [Fact]
    public async Task AJpegWithThousandsOfScansIsAProcessingLimit()
    {
        // Variante pequena do ataque medido (135 mil scans num JPEG de 20 MP, 84 s): 5 mil scans num 64×32, que o
        // decoder aceitaria em instantes se a defesa sumisse.
        var jpeg = TestImages.ProgressiveJpegWithScans(5000);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit), await SanitizeAsync(PhotoFormat.Jpeg, jpeg));
    }

    [Fact]
    public async Task AnOriginalOverTheUploadCeilingIsNotDecoded()
    {
        // A recepção já recusa acima de 5 MiB; o sanitizador não depende disso para não decodificar o que passar.
        byte[] oversized = [.. TestImages.Jpeg(), .. new byte[PhotoLimits.MaxBytes]];

        Assert.Equal(
            new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit),
            await SanitizeAsync(PhotoFormat.Jpeg, oversized));
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

    /// <summary>
    /// Arquivos completos (passam pela completude) mas corrompidos por dentro: só o decoder percebe. Prova o caminho de
    /// falha fechada: o desfecho é rejeição, nunca o original publicado nem uma exceção que vire nova tentativa.
    /// </summary>
    public static TheoryData<string, PhotoFormat> CompleteButCorrupt => new()
    {
        { "png-wrong-crc", PhotoFormat.Png },
        { "png-garbage-idat", PhotoFormat.Png },
        { "jpeg-tampered-dht", PhotoFormat.Jpeg },
    };

    [Theory]
    [MemberData(nameof(CompleteButCorrupt))]
    public async Task ACompleteButCorruptFileIsACorruptImage(string fixture, PhotoFormat format)
    {
        var bytes = CorruptFixture(fixture);
        Assert.True(PhotoCompleteness.IsComplete(format, bytes), "A fixture deveria passar pela completude.");

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.CorruptImage), await SanitizeAsync(format, bytes));
    }

    internal static byte[] CorruptFixture(string fixture) => fixture switch
    {
        "png-wrong-crc" => TestImages.PngWithWrongCrc(),
        "png-garbage-idat" => TestImages.PngWithGarbageCompressedData(),
        "jpeg-tampered-dht" => TestImages.JpegWithTamperedHuffmanTable(),
        _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, "Fixture sem gerador."),
    };

    [Fact]
    public async Task AnImageOverTheMemoryLimitIsAProcessingLimit()
    {
        var tight = new ImageSharpPhotoSanitizer(new PhotoSanitizerOptions { MemoryLimitMegabytes = 1 });

        var outcome = await tight.SanitizeAsync(PhotoFormat.Png, TestImages.Png(2000, 2000), TestContext.Current.CancellationToken);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit), outcome);
    }

    /// <summary>
    /// O prazo é cooperativo (o token do ImageSharp). Este teste prova que um prazo vencido vira PROCESSING_LIMIT, não
    /// exceção; o tempo de cada etapa é limitado pelo teto de 20 MP, pelo paralelismo 1 e por <see cref="PhotoDecodeCost"/>.
    /// </summary>
    [Fact]
    public async Task AnExpiredTimeLimitIsAProcessingLimitAtTheNextStep()
    {
        var hurried = new ImageSharpPhotoSanitizer(new PhotoSanitizerOptions { Timeout = TimeSpan.FromTicks(1) });

        var outcome = await hurried.SanitizeAsync(PhotoFormat.Png, TestImages.Png(3000, 3000), TestContext.Current.CancellationToken);

        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit), outcome);
    }

    private static readonly string[] ImageOnlyChunks = ["VP8 ", "VP8L"];

    private static bool IsReddish(Rgba32 pixel) => pixel.R > 200 && pixel.B < 80;

    private Task<SanitizeOutcome> SanitizeAsync(PhotoFormat declared, byte[] bytes) =>
        _sanitizer.SanitizeAsync(declared, bytes, TestContext.Current.CancellationToken);
}
