using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class ReviewPromotionKeyTests
{
    [Theory]
    [InlineData(PhotoVariant.Thumb, "thumb")]
    [InlineData(PhotoVariant.Card, "card")]
    [InlineData(PhotoVariant.Full, "full")]
    public void PromotionKeysCannotOverwriteALegacyDerivativeWithTheSameToken(PhotoVariant variant, string suffix)
    {
        var generation = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var legacy = ObjectKey.ForDerivative(ObjectKey.Parse("quarantine/0123456789abcdef0123456789abcdef"), variant);

        var promoted = ObjectKey.ForPromotedDerivative(generation, variant);

        Assert.Equal($"derivatives/review-0123456789abcdef0123456789abcdef-{suffix}", promoted.Value);
        Assert.Equal(StorageArea.Derivatives, promoted.Area);
        Assert.NotEqual(legacy, promoted);
        Assert.Equal($"derivatives/0123456789abcdef0123456789abcdef-{suffix}", legacy.Value);
        Assert.Equal(promoted, ObjectKey.Parse(promoted.Value));
        Assert.Equal("ObjectKey(derivatives)", promoted.ToString());
        Assert.DoesNotContain("0123456789abcdef", promoted.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(promoted, ObjectKey.ForPromotedDerivative(Guid.NewGuid(), variant));
    }

    [Fact]
    public void PromotionKeysRejectZeroGenerationsAndUnknownVariants()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectKey.ForPromotedDerivative(Guid.Empty, PhotoVariant.Card));
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectKey.ForPromotedDerivative(Guid.NewGuid(), (PhotoVariant)999));
    }

    [Theory]
    [InlineData("derivatives/review-00000000000000000000000000000000-card")]
    [InlineData("derivatives/review-0123456789ABCDEF0123456789ABCDEF-card")]
    [InlineData("derivatives/review-0123456789abcdef0123456789abcdef-original")]
    [InlineData("derivatives/review-0123456789abcdef0123456789abcdef-card\n")]
    [InlineData("derivatives/review-0123456789abcdef0123456789abcdef-card/../full")]
    [InlineData("/derivatives/review-0123456789abcdef0123456789abcdef-card")]
    [InlineData("derivatives\\review-0123456789abcdef0123456789abcdef-card")]
    public void PromotionParserNeverAcceptsANoncanonicalKey(string stored)
    {
        Assert.Throws<FormatException>(() => ObjectKey.Parse(stored));
    }
}
