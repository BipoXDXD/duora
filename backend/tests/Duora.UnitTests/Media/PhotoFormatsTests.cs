using System.Text;
using Duora.Core.Media;
using static Duora.UnitTests.Media.ImageBytes;

namespace Duora.UnitTests.Media;

public sealed class PhotoFormatsTests
{
    [Theory]
    [InlineData("image/jpeg", PhotoFormat.Jpeg)]
    [InlineData("image/png", PhotoFormat.Png)]
    [InlineData("image/webp", PhotoFormat.WebP)]
    [InlineData("IMAGE/JPEG", PhotoFormat.Jpeg)]
    public void TheDeclaredContentTypeIsOneOfThreeAllowedTypes(string contentType, PhotoFormat expected)
    {
        Assert.True(PhotoFormats.TryParseContentType(contentType, out var format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("image/heic")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData("image/jpg")]
    [InlineData("image/jpeg; charset=utf-8")]
    [InlineData(" image/jpeg")]
    [InlineData("multipart/form-data; boundary=x")]
    public void AnyOtherContentTypeIsRefused(string? contentType)
    {
        Assert.False(PhotoFormats.TryParseContentType(contentType, out _));
    }

    [Theory]
    [InlineData(PhotoFormat.Jpeg, "image/jpeg")]
    [InlineData(PhotoFormat.Png, "image/png")]
    [InlineData(PhotoFormat.WebP, "image/webp")]
    public void EachFormatHasItsCanonicalContentType(PhotoFormat format, string expected)
    {
        Assert.Equal(expected, PhotoFormats.ContentType(format));
    }

    [Fact]
    public void JpegIsRecognizedByItsStartOfImageMarker()
    {
        Assert.Equal(PhotoFormat.Jpeg, PhotoFormats.Sniff(Jpeg()));
    }

    [Fact]
    public void PngIsRecognizedByItsEightByteSignature()
    {
        Assert.Equal(PhotoFormat.Png, PhotoFormats.Sniff(Png(Ihdr())));
    }

    [Fact]
    public void WebPIsRecognizedByTheRiffContainerAndTheWebpForm()
    {
        Assert.Equal(PhotoFormat.WebP, PhotoFormats.Sniff(WebP(Chunk("VP8 ", 10))));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg/>")]
    [InlineData("<!DOCTYPE html><html><body>oi</body></html>")]
    [InlineData("MZ\u0090\u0000\u0003\u0000\u0000\u0000")]
    [InlineData("\u007FELF\u0002\u0001\u0001")]
    [InlineData("GIF89a\u0001\u0000\u0001\u0000")]
    [InlineData("%PDF-1.7")]
    public void TextAndExecutableSignaturesAreNotImages(string content)
    {
        Assert.Null(PhotoFormats.Sniff(Encoding.Latin1.GetBytes(content)));
    }

    [Fact]
    public void ARiffFileThatIsNotWebPIsNotAnImage()
    {
        var wave = WebP(Chunk("fmt ", 16));
        "WAVE"u8.CopyTo(wave.AsSpan(8));

        Assert.Null(PhotoFormats.Sniff(wave));
    }

    [Theory]
    [InlineData("jpeg", 2)]
    [InlineData("png", 7)]
    [InlineData("webp", 11)]
    [InlineData("png", 0)]
    public void AHeaderCutBeforeTheEndOfTheSignatureIsNotAnImage(string format, int length)
    {
        var complete = format switch
        {
            "jpeg" => Jpeg(),
            "png" => Png(Ihdr()),
            _ => WebP(Chunk("VP8 ", 10)),
        };

        Assert.Null(PhotoFormats.Sniff(complete.AsSpan(0, length)));
    }

    [Fact]
    public void APolyglotIsJudgedByItsFirstBytesOnly()
    {
        // HTML com bytes de JPEG no meio: o começo é texto, então não é imagem.
        var polyglot = Encoding.ASCII.GetBytes("<html><script>alert(1)</script>").Concat(Jpeg()).ToArray();

        Assert.Null(PhotoFormats.Sniff(polyglot));
    }
}
