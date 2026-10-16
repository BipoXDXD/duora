using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class UploadFingerprintTests
{
    private static readonly byte[] HashA = Enumerable.Repeat((byte)0xAA, 32).ToArray();
    private static readonly byte[] HashB = Enumerable.Repeat((byte)0xBB, 32).ToArray();

    [Fact]
    public void TheSameBytesWithTheSameTypeReplayTheFirstUpload()
    {
        var first = new UploadFingerprint(PhotoFormat.Png, 100, HashA);
        var again = new UploadFingerprint(PhotoFormat.Png, 100, [.. HashA]);

        Assert.Equal(IdempotentOutcome.Replay, UploadFingerprint.Compare(first, again));
    }

    [Fact]
    public void DifferentBytesUnderTheSameKeyAreAReusedKey()
    {
        var first = new UploadFingerprint(PhotoFormat.Png, 100, HashA);
        var other = new UploadFingerprint(PhotoFormat.Png, 100, HashB);

        Assert.Equal(IdempotentOutcome.KeyReused, UploadFingerprint.Compare(first, other));
    }

    [Fact]
    public void TheSameBytesDeclaredAsAnotherTypeAreAReusedKey()
    {
        var first = new UploadFingerprint(PhotoFormat.Png, 100, HashA);
        var other = new UploadFingerprint(PhotoFormat.WebP, 100, HashA);

        Assert.Equal(IdempotentOutcome.KeyReused, UploadFingerprint.Compare(first, other));
    }

    [Fact]
    public void ADifferentSizeIsAReusedKey()
    {
        var first = new UploadFingerprint(PhotoFormat.Png, 100, HashA);
        var other = new UploadFingerprint(PhotoFormat.Png, 101, HashA);

        Assert.Equal(IdempotentOutcome.KeyReused, UploadFingerprint.Compare(first, other));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void TheHashIsAlwaysThirtyTwoBytes(int length)
    {
        Assert.Throws<ArgumentException>(() => new UploadFingerprint(PhotoFormat.Png, 100, new byte[length]));
    }

    [Fact]
    public void TheFingerprintKeepsItsOwnCopyOfTheHash()
    {
        var hash = (byte[])HashA.Clone();
        var fingerprint = new UploadFingerprint(PhotoFormat.Png, 100, hash);

        hash[0] = 0;

        Assert.Equal(0xAA, fingerprint.Sha256.Span[0]);
    }
}
