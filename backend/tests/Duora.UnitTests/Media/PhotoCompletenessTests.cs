using Duora.Core.Media;
using static Duora.UnitTests.Media.ImageBytes;

namespace Duora.UnitTests.Media;

public sealed class PhotoCompletenessTests
{
    private static readonly byte[] StartOfScan = [0xFF, 0xDA, 0x00, 0x02];
    private static readonly byte[] EndOfImage = [0xFF, 0xD9];

    [Fact]
    public void AJpegEndsWithTheEndOfImageMarkerAfterTheScan()
    {
        byte[] jpeg = [.. Jpeg(), .. StartOfScan, 0x11, 0x22, .. EndOfImage];

        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, jpeg));
    }

    [Fact]
    public void AJpegWithBytesAfterTheEndOfImageIsStillComplete()
    {
        // Poliglota JPEG + HTML: completo; o reencode descarta o que vem depois.
        byte[] jpeg = [.. Jpeg(), .. StartOfScan, 0x11, .. EndOfImage, (byte)'<', (byte)'h'];

        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, jpeg));
    }

    [Fact]
    public void AJpegCutInsideTheScanIsIncomplete()
    {
        byte[] jpeg = [.. Jpeg(), .. StartOfScan, 0x11, 0x22, 0x33];

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, jpeg));
    }

    [Fact]
    public void AnEndMarkerOnlyBeforeTheScanDoesNotCount()
    {
        // Miniatura do EXIF tem o próprio FF D9 antes do scan principal.
        byte[] jpeg = [.. Jpeg(), .. EndOfImage, .. StartOfScan, 0x11];

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, jpeg));
    }

    [Fact]
    public void APngIsCompleteWithItsEndChunk()
    {
        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Png, Png(Ihdr(), Chunk("IDAT", 4), Chunk("IEND"))));
    }

    [Fact]
    public void APngWithoutTheEndChunkIsIncomplete()
    {
        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Png, Png(Ihdr(), Chunk("IDAT", 4))));
    }

    [Fact]
    public void APngCutInsideAChunkIsIncomplete()
    {
        var png = Png(Ihdr(), Chunk("IDAT", 40), Chunk("IEND"));

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Png, png.AsSpan(0, 40)));
    }

    [Fact]
    public void AWebPHoldsAllTheBytesItsRiffHeaderDeclares()
    {
        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.WebP, WebP(Chunk("VP8 ", 30))));
    }

    [Fact]
    public void AWebPShorterThanItsRiffHeaderIsIncomplete()
    {
        var webp = WebP(Chunk("VP8 ", 30));

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.WebP, webp.AsSpan(0, webp.Length - 5)));
    }
}
