using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class PhotoInspectionTests
{
    [Fact]
    public void TheDetectedTypeMatchingTheDeclaredOnePasses()
    {
        Assert.Null(PhotoInspection.CheckSignature(PhotoFormat.Png, PhotoFormat.Png));
    }

    [Fact]
    public void AFileThatIsNoAllowedImageIsAnUnsupportedFormat()
    {
        Assert.Equal(PhotoRejection.UnsupportedFormat, PhotoInspection.CheckSignature(PhotoFormat.Jpeg, detected: null));
    }

    [Theory]
    [InlineData(PhotoFormat.Jpeg, PhotoFormat.Png)]
    [InlineData(PhotoFormat.Png, PhotoFormat.WebP)]
    [InlineData(PhotoFormat.WebP, PhotoFormat.Jpeg)]
    public void ADetectedTypeOtherThanTheDeclaredOneIsAMismatch(PhotoFormat declared, PhotoFormat detected)
    {
        Assert.Equal(PhotoRejection.TypeMismatch, PhotoInspection.CheckSignature(declared, detected));
    }

    [Theory]
    [InlineData(5000, 4000)]
    [InlineData(20_000_000, 1)]
    [InlineData(1, 1)]
    public void UpToTwentyMegapixelsFromTheHeaderPass(int width, int height)
    {
        Assert.Null(PhotoInspection.CheckHeader(width, height, frameCount: 1));
    }

    [Theory]
    [InlineData(5001, 4000)]
    [InlineData(20_000_001, 1)]
    [InlineData(30_000, 30_000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void MoreThanTwentyMegapixelsIsRejectedBeforeDecoding(int width, int height)
    {
        Assert.Equal(PhotoRejection.TooManyPixels, PhotoInspection.CheckHeader(width, height, frameCount: 1));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    public void ZeroOrNegativeDimensionsAreACorruptImage(int width, int height)
    {
        Assert.Equal(PhotoRejection.CorruptImage, PhotoInspection.CheckHeader(width, height, frameCount: 1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(100)]
    public void MoreThanOneFrameIsAnAnimation(int frames)
    {
        Assert.Equal(PhotoRejection.Animated, PhotoInspection.CheckHeader(100, 100, frames));
    }

    [Fact]
    public void ZeroFramesIsACorruptImage()
    {
        Assert.Equal(PhotoRejection.CorruptImage, PhotoInspection.CheckHeader(100, 100, frameCount: 0));
    }

    [Theory]
    [InlineData(PhotoRejection.UnsupportedFormat, "UNSUPPORTED_FORMAT")]
    [InlineData(PhotoRejection.TypeMismatch, "TYPE_MISMATCH")]
    [InlineData(PhotoRejection.TooManyPixels, "TOO_MANY_PIXELS")]
    [InlineData(PhotoRejection.Animated, "ANIMATED")]
    [InlineData(PhotoRejection.CorruptImage, "CORRUPT_IMAGE")]
    [InlineData(PhotoRejection.ProcessingLimit, "PROCESSING_LIMIT")]
    public void EachRejectionHasAStableCode(PhotoRejection rejection, string code)
    {
        Assert.Equal(code, PhotoRejections.Code(rejection));
        Assert.Equal(rejection, PhotoRejections.FromCode(code));
    }

    [Fact]
    public void AnUnknownStoredCodeIsNotARejection()
    {
        Assert.Null(PhotoRejections.FromCode("SOMETHING_ELSE"));
    }
}
