using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class PhotoVariantsTests
{
    [Theory]
    [InlineData(PhotoVariant.Thumb, 256)]
    [InlineData(PhotoVariant.Card, 768)]
    [InlineData(PhotoVariant.Full, 1600)]
    public void EachVariantHasAMaximumLongestSide(PhotoVariant variant, int maxSide)
    {
        Assert.Equal(maxSide, PhotoVariants.MaxSide(variant));
    }

    [Theory]
    [InlineData(PhotoVariant.Thumb, "thumb")]
    [InlineData(PhotoVariant.Card, "card")]
    [InlineData(PhotoVariant.Full, "full")]
    public void EachVariantHasAStableStoredName(PhotoVariant variant, string name)
    {
        Assert.Equal(name, PhotoVariants.Name(variant));
    }

    [Theory]
    [InlineData(4000, 3000, 1600, 1600, 1200)]
    [InlineData(3000, 4000, 1600, 1200, 1600)]
    [InlineData(1000, 1000, 256, 256, 256)]
    [InlineData(20_000_000, 1, 256, 256, 1)]
    [InlineData(1001, 333, 768, 768, 255)]
    public void ALargerImageShrinksToTheLongestSideKeepingTheAspectRatio(
        int width, int height, int maxSide, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), PhotoVariants.Fit(width, height, maxSide));
    }

    [Theory]
    [InlineData(800, 600, 1600)]
    [InlineData(256, 100, 256)]
    [InlineData(1, 1, 256)]
    public void ASmallerImageIsNeverEnlarged(int width, int height, int maxSide)
    {
        Assert.Equal((width, height), PhotoVariants.Fit(width, height, maxSide));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void DimensionsMustBePositive(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PhotoVariants.Fit(width, height, 256));
    }
}
