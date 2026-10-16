using System.Text;
using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

/// <summary>RFC 6238, apêndice B (HMAC-SHA1), truncado nos 6 dígitos que os autenticadores mostram.</summary>
public sealed class TotpTests
{
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void CodesMatchTheRfcVectors(long unixSeconds, string expected)
    {
        Assert.Equal(expected, Totp.CodeAt(RfcKey, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void TheCodeOfTheCurrentOrANeighbourStepIsAcceptedAndItsStepReturned(int offset)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        var step = Totp.StepAt(now) + offset;

        Assert.Equal(step, Totp.MatchStep(RfcKey, Totp.CodeAt(RfcKey, step), now));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void TheCodeOfAStepTwoAwayIsRejected(int offset)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);

        Assert.Null(Totp.MatchStep(RfcKey, Totp.CodeAt(RfcKey, Totp.StepAt(now) + offset), now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    [InlineData("000000")]
    public void MalformedOrWrongCodesAreRejected(string code)
    {
        Assert.Null(Totp.MatchStep(RfcKey, code, DateTimeOffset.FromUnixTimeSeconds(1111111111)));
    }

    [Fact]
    public void Base32KeysDecodeLikeTheAuthenticatorApps()
    {
        Assert.Equal(RfcKey, Totp.DecodeBase32Key("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"));
        Assert.Equal(RfcKey, Totp.DecodeBase32Key("gezd gnbv gy3t qojq gezd gnbv gy3t qojq"));
    }

    [Theory]
    [InlineData("GEZDGNBV1")]
    [InlineData("")]
    public void InvalidBase32KeysAreRejected(string key)
    {
        Assert.Throws<FormatException>(() => Totp.DecodeBase32Key(key));
    }

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123 456 ", "123456")]
    [InlineData("12 34 56", "123456")]
    public void UserInputIsNormalizedBySpaces(string typed, string normalized)
    {
        Assert.Equal(normalized, Totp.NormalizeInput(typed));
    }
}
