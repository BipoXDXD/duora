using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class ReviewCandidateKeyTests
{
    [Theory]
    [InlineData("thumb")]
    [InlineData("card")]
    [InlineData("full")]
    public void StoredCandidateKeysUseASeparatePrivateAreaAndNeverPrintTheirGeneration(string variant)
    {
        var generation = Guid.NewGuid().ToString("N");
        var stored = $"review-candidates/{generation}-{variant}";

        var key = ObjectKey.Parse(stored);

        Assert.Equal("ReviewCandidates", key.Area.ToString());
        Assert.Equal(stored, key.Value);
        Assert.Equal("ObjectKey(review-candidates)", key.ToString());
        Assert.DoesNotContain(generation, key.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ObjectKey.ForDerivative(key, PhotoVariant.Card));
    }
}
