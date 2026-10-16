using Duora.Core.Media;
using static Duora.UnitTests.Media.ImageBytes;

namespace Duora.UnitTests.Media;

public sealed class PhotoAnimationTests
{
    [Fact]
    public void AStaticPngHasNoAnimationControlChunk()
    {
        var png = Png(Ihdr(), Chunk("IDAT", 4), Chunk("IEND"));

        Assert.False(PhotoAnimation.IsAnimated(PhotoFormat.Png, png));
    }

    [Fact]
    public void AnApngDeclaresAnimationControlBeforeTheImageData()
    {
        var apng = Png(Ihdr(), Chunk("acTL", 8), Chunk("fcTL", 26), Chunk("IDAT", 4), Chunk("IEND"));

        Assert.True(PhotoAnimation.IsAnimated(PhotoFormat.Png, apng));
    }

    [Fact]
    public void AnAnimationControlChunkAfterTheImageDataIsStillRejected()
    {
        // A especificação manda acTL antes do IDAT; um arquivo fora da ordem não ganha o benefício da dúvida.
        var png = Png(Ihdr(), Chunk("IDAT", 4), Chunk("acTL", 8), Chunk("IEND"));

        Assert.True(PhotoAnimation.IsAnimated(PhotoFormat.Png, png));
    }

    [Fact]
    public void APngWithAChunkLengthBeyondTheFileStopsTheScanWithoutThrowing()
    {
        var png = Png(Ihdr());
        png[8] = 0x7F;

        Assert.False(PhotoAnimation.IsAnimated(PhotoFormat.Png, png));
    }

    [Fact]
    public void ASimpleLossyWebPIsStatic()
    {
        Assert.False(PhotoAnimation.IsAnimated(PhotoFormat.WebP, WebP(Chunk("VP8 ", 10))));
    }

    [Fact]
    public void AnExtendedWebPWithoutTheAnimationFlagIsStatic()
    {
        var webp = WebP(Vp8x(flags: 0x10), Chunk("VP8L", 5));

        Assert.False(PhotoAnimation.IsAnimated(PhotoFormat.WebP, webp));
    }

    [Fact]
    public void AnExtendedWebPWithTheAnimationFlagIsAnimated()
    {
        var webp = WebP(Vp8x(flags: 0x02), Chunk("ANIM", 6), Chunk("ANMF", 16));

        Assert.True(PhotoAnimation.IsAnimated(PhotoFormat.WebP, webp));
    }

    [Theory]
    [InlineData("ANIM")]
    [InlineData("ANMF")]
    public void AnimationChunksMakeAWebPAnimatedEvenWithoutTheFlag(string fourCc)
    {
        var webp = WebP(Vp8x(flags: 0x00), Chunk(fourCc, 6), Chunk("VP8 ", 10));

        Assert.True(PhotoAnimation.IsAnimated(PhotoFormat.WebP, webp));
    }

    [Fact]
    public void OddSizedWebPChunksArePaddedAndTheScanFindsTheNextChunk()
    {
        var webp = WebP(Vp8x(flags: 0x00), Chunk("ICCP", 3), Chunk("ANMF", 16));

        Assert.True(PhotoAnimation.IsAnimated(PhotoFormat.WebP, webp));
    }

    [Fact]
    public void JpegHasNoAnimation()
    {
        Assert.False(PhotoAnimation.IsAnimated(PhotoFormat.Jpeg, Jpeg()));
    }
}
