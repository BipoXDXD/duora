using Duora.Core.Media;
using static Duora.UnitTests.Media.ImageBytes;

namespace Duora.UnitTests.Media;

public sealed class PhotoCompletenessTests
{
    [Fact]
    public void AJpegEndsWithTheEndOfImageMarkerAfterTheScan()
    {
        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), Scan(), EndOfImage)));
    }

    [Fact]
    public void AJpegWithBytesAfterTheEndOfImageIsStillComplete()
    {
        // Poliglota JPEG + HTML: completo; o reencode descarta o que vem depois.
        byte[] jpeg = [.. JpegFile(Header(), Scan(), EndOfImage), (byte)'<', (byte)'h'];

        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, jpeg));
    }

    [Fact]
    public void AJpegCutInsideTheScanIsIncomplete()
    {
        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), Scan())));
    }

    [Fact]
    public void AnEndMarkerOnlyBeforeTheScanDoesNotCount()
    {
        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), EndOfImage, Scan())));
    }

    [Theory]
    [InlineData(App1)]
    [InlineData(Comment)]
    public void AThumbnailInsideAMetadataSegmentDoesNotCompleteACutImage(byte marker)
    {
        // A miniatura do EXIF é um JPEG inteiro (com SOS e EOI próprios) dentro do APP1; o arquivo principal termina
        // no meio do scan, como um upload de celular cortado.
        var jpeg = JpegFile(Segment(marker, Thumbnail()), Header(), Scan());

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, jpeg));
    }

    [Theory]
    [InlineData(App1)]
    [InlineData(Comment)]
    public void AnIntactJpegWithAThumbnailIsComplete(byte marker)
    {
        var jpeg = JpegFile(Segment(marker, Thumbnail()), Header(), Scan(), EndOfImage);

        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, jpeg));
    }

    [Fact]
    public void ASegmentLongerThanTheFileIsIncomplete()
    {
        byte[] jpeg = [.. JpegFile(Header()), Marker, App1, 0x7F, 0xFF, .. Thumbnail(), .. Scan(), .. EndOfImage];

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, jpeg));
    }

    [Fact]
    public void ASegmentShorterThanItsContentIsIncomplete()
    {
        // O comprimento declarado cobre só metade da miniatura: o próximo byte já não é um marcador.
        var thumbnail = Thumbnail();
        byte[] lying = [Marker, App1, 0x00, (byte)(2 + (thumbnail.Length / 2)), .. thumbnail];

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(lying, Header(), Scan(), EndOfImage)));
    }

    [Fact]
    public void StuffedAndRestartBytesInsideTheScanAreNotTheEnd()
    {
        // FF 00 é um FF literal do dado comprimido e FF D0 é restart: nenhum dos dois encerra a imagem.
        byte[] scan = [.. Scan(), Marker, 0x00, 0x33, Marker, 0xD0, 0x44];

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), scan)));
        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), scan, EndOfImage)));
    }

    [Fact]
    public void AProgressiveJpegNeedsTheEndAfterItsLastScan()
    {
        // Progressivo: vários scans, com tabelas entre eles. Cortado depois do primeiro scan, está incompleto.
        var firstScan = JpegFile(Header(), Scan(), Segment(DefineHuffmanTable, new byte[17]));

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, firstScan));
        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, [.. firstScan, .. Scan(), .. EndOfImage]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ASegmentLengthShorterThanItsOwnLengthFieldIsIncomplete(byte length)
    {
        // O comprimento conta os próprios dois bytes: 0 e 1 não descrevem segmento nenhum.
        byte[] impossible = [Marker, App1, 0x00, length];

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), impossible, Scan(), EndOfImage)));
    }

    [Fact]
    public void FillBytesBeforeAMarkerAreSkipped()
    {
        // Qualquer quantidade de FF pode preceder um marcador (T.81 B.1.1.2), entre segmentos e antes do EOI.
        byte[] fill = [Marker, Marker];

        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), fill, Scan(), fill, EndOfImage)));
    }

    [Fact]
    public void FillBytesInsideTheScanDoNotEndIt()
    {
        // Preenchimento antes de um restart, no meio do dado comprimido.
        byte[] scan = [.. Scan(), Marker, Marker, 0xD0, 0x55];

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), scan)));
        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), scan, EndOfImage)));
    }

    [Theory]
    [InlineData(0xD0)]
    [InlineData(0xD3)]
    [InlineData(0xD7)]
    public void EveryRestartMarkerKeepsTheScanOpen(byte restart)
    {
        byte[] scan = [.. Scan(), Marker, restart, 0x66, Marker, 0x00];

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), scan)));
        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), scan, EndOfImage)));
    }

    [Fact]
    public void AProgressiveJpegCutInsideItsSecondScanIsIncomplete()
    {
        var tables = Segment(DefineHuffmanTable, new byte[17]);

        Assert.False(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), Scan(), tables, Scan())));
        Assert.True(PhotoCompleteness.IsComplete(PhotoFormat.Jpeg, JpegFile(Header(), Scan(), tables, Scan(), EndOfImage)));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0x00, 0x00, 0x02, 0xFF, 0xD9 })]
    public void AJpegWithoutAScanIsIncomplete(byte[] jpeg)
    {
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
