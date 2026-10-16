using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class PhotoUploadRequestTests
{
    [Theory]
    [InlineData(1L)]
    [InlineData(5L * 1024 * 1024)]
    public void ALengthFromOneByteToFiveMebibytesIsAccepted(long length)
    {
        Assert.Equal(UploadLengthVerdict.Accepted, PhotoLimits.CheckContentLength(length));
    }

    [Fact]
    public void FiveMebibytesPlusOneByteIsTooLarge()
    {
        Assert.Equal(UploadLengthVerdict.TooLarge, PhotoLimits.CheckContentLength((5L * 1024 * 1024) + 1));
    }

    [Fact]
    public void AMissingLengthIsRefusedBeforeReadingTheBody()
    {
        Assert.Equal(UploadLengthVerdict.Missing, PhotoLimits.CheckContentLength(null));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AnEmptyBodyIsRefused(long length)
    {
        Assert.Equal(UploadLengthVerdict.Empty, PhotoLimits.CheckContentLength(length));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("0f8e6c1a-4d2b-4b8f-9c2e-6a1d3e5f7a9b")]
    [InlineData("upload_2026-10-15_Z")]
    public void AnIdempotencyKeyIsOneToSixtyFourSafeCharacters(string value)
    {
        Assert.True(IdempotencyKey.TryParse(value, out var key));
        Assert.Equal(value, key.Value);
    }

    [Fact]
    public void SixtyFourCharactersIsTheLongestKey()
    {
        Assert.True(IdempotencyKey.TryParse(new string('k', 64), out _));
        Assert.False(IdempotencyKey.TryParse(new string('k', 65), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("chave com espaço")]
    [InlineData("../../etc/passwd")]
    [InlineData("key\u0000")]
    [InlineData("chaveç")]
    [InlineData("a,b")]
    public void AnyOtherKeyIsRefused(string? value)
    {
        Assert.False(IdempotencyKey.TryParse(value, out _));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(10, false)]
    public void ThePendingQuotaAdmitsANewUploadOnlyBelowTheCap(int pending, bool admitted)
    {
        var quota = new PendingUploadQuota(3);

        Assert.Equal(admitted, quota.Admits(pending));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ThePendingCapIsAtLeastOne(int cap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PendingUploadQuota(cap));
    }
}
