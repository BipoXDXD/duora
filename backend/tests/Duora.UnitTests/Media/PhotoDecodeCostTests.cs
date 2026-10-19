using Duora.Core.Media;
using static Duora.UnitTests.Media.ImageBytes;

namespace Duora.UnitTests.Media;

public sealed class PhotoDecodeCostTests
{
    [Fact]
    public void AJpegWithTheMaximumNumberOfScansIsWithinTheLimits()
    {
        Assert.True(PhotoDecodeCost.IsWithinLimits(PhotoFormat.Jpeg, JpegWithScans(100)));
    }

    [Fact]
    public void AJpegWithOneScanOverTheMaximumIsNot()
    {
        // Cada scan progressivo percorre os blocos da imagem inteira: milhares de scans quase vazios num arquivo de
        // 5 MiB prendem o decoder por minutos (medido: 135 mil scans num JPEG de 20 MP, 84 s).
        Assert.False(PhotoDecodeCost.IsWithinLimits(PhotoFormat.Jpeg, JpegWithScans(101)));
    }

    [Fact]
    public void ScansOfAThumbnailInsideAMetadataSegmentDoNotCount()
    {
        var thumbnails = Enumerable.Repeat(Segment(App1, Thumbnail()), 101).ToArray();
        var jpeg = JpegFile([.. thumbnails, Header(), Scan(), EndOfImage]);

        Assert.True(PhotoDecodeCost.IsWithinLimits(PhotoFormat.Jpeg, jpeg));
    }

    [Theory]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    [InlineData("iCCP")]
    public void APngWithTheMaximumNumberOfInflatedChunksIsWithinTheLimits(string type)
    {
        Assert.True(PhotoDecodeCost.IsWithinLimits(PhotoFormat.Png, PngWithChunks(type, 4)));
    }

    [Theory]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    [InlineData("iCCP")]
    public void APngWithOneInflatedChunkOverTheMaximumIsNot(string type)
    {
        // Cada chunk comprimido vira até 8 MB fora do alocador limitado do decoder: medido, 640 zTXt num PNG de 5 MB
        // levam o processo a 3,3 GB.
        Assert.False(PhotoDecodeCost.IsWithinLimits(PhotoFormat.Png, PngWithChunks(type, 5)));
    }

    [Fact]
    public void DifferentInflatedChunksAddUp()
    {
        // 1 iCCP + 2 zTXt + 2 iTXt = 5, um acima do teto de 4.
        var png = Png(
        [
            Ihdr(),
            Chunk("iCCP", 4),
            .. Enumerable.Repeat(Chunk("zTXt", 4), 2),
            .. Enumerable.Repeat(Chunk("iTXt", 4), 2),
            Chunk("IEND"),
        ]);

        Assert.False(PhotoDecodeCost.IsWithinLimits(PhotoFormat.Png, png));
    }

    [Fact]
    public void UncompressedTextChunksDoNotCount()
    {
        // tEXt é lido como está: o tamanho já é limitado pelo do arquivo.
        Assert.True(PhotoDecodeCost.IsWithinLimits(PhotoFormat.Png, PngWithChunks("tEXt", 1000)));
    }

    [Fact]
    public void AWebPIsWithinTheLimits()
    {
        Assert.True(PhotoDecodeCost.IsWithinLimits(PhotoFormat.WebP, WebP(Vp8x(0))));
    }

    private static byte[] JpegWithScans(int scans) =>
        JpegFile([Header(), .. Enumerable.Repeat(Scan(), scans), EndOfImage]);

    private static byte[] PngWithChunks(string type, int count) =>
        Png([Ihdr(), .. Enumerable.Repeat(Chunk(type, 4), count), Chunk("IDAT", 4), Chunk("IEND")]);
}
