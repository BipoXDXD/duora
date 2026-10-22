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

    [Theory]
    [InlineData(PhotoVariant.Thumb)]
    [InlineData(PhotoVariant.Card)]
    [InlineData(PhotoVariant.Full)]
    public void CandidateKeysRoundTripAndDifferentGenerationsNeverShareAKey(PhotoVariant variant)
    {
        var generation = Guid.NewGuid();
        var key = ObjectKey.ForReviewCandidate(generation, variant);

        Assert.Equal(StorageArea.ReviewCandidates, key.Area);
        Assert.Equal($"review-candidates/{generation:N}-{PhotoVariants.Name(variant)}", key.Value);
        Assert.Equal(key, ObjectKey.Parse(key.Value));
        Assert.Equal(key, ObjectKey.ForReviewCandidate(generation, variant));
        Assert.NotEqual(key, ObjectKey.ForReviewCandidate(Guid.NewGuid(), variant));
    }

    [Fact]
    public void CandidateKeysRejectEmptyGenerationsAndUnknownVariants()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectKey.ForReviewCandidate(Guid.Empty, PhotoVariant.Card));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectKey.ForReviewCandidate(Guid.NewGuid(), (PhotoVariant)999));
    }

    [Theory]
    [InlineData("review-candidates/")]
    [InlineData("review-candidates/00000000000000000000000000000000-card")]
    [InlineData("review-candidates/0123456789ABCDEF0123456789ABCDEF-card")]
    [InlineData("review-candidates/0123456789abcdef0123456789abcdef-original")]
    [InlineData("review-candidates/0123456789abcdef0123456789abcdef-card\n")]
    [InlineData("review-candidates/0123456789abcdef0123456789abcdef-card/../full")]
    [InlineData("review-candidates/../derivatives/0123456789abcdef0123456789abcdef-card")]
    [InlineData("/review-candidates/0123456789abcdef0123456789abcdef-card")]
    [InlineData("review-candidates\\0123456789abcdef0123456789abcdef-card")]
    public void MalformedCandidateKeysAreNeverAccepted(string stored)
    {
        Assert.Throws<FormatException>(() => ObjectKey.Parse(stored));
    }
}
