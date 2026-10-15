using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("ana@example.com")]
    [InlineData("a@b.co")]
    [InlineData("ana.souza+duora@sub.example.com.br")]
    public void TryParseAcceptsPlainAddresses(string value)
    {
        Assert.True(EmailAddress.TryParse(value, out var address));
        Assert.Equal(value, address!.Value);
    }

    [Fact]
    public void TryParseAcceptsTheMaximumLengthOf254()
    {
        var value = new string('a', 64) + "@" + new string('b', 185) + ".com";

        Assert.Equal(254, value.Length);
        Assert.True(EmailAddress.TryParse(value, out _));
    }

    [Fact]
    public void TryParseRejectsOneCharacterAboveTheMaximum()
    {
        var value = new string('a', 64) + "@" + new string('b', 186) + ".com";

        Assert.Equal(255, value.Length);
        Assert.False(EmailAddress.TryParse(value, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ana")]
    [InlineData("ana@")]
    [InlineData("@example.com")]
    [InlineData("ana@localhost")]
    [InlineData(" ana@example.com")]
    [InlineData("ana@example.com ")]
    [InlineData("Ana <ana@example.com>")]
    [InlineData("ana@exa mple.com")]
    [InlineData("ana\u0000@example.com")]
    [InlineData("ana@example.com\n")]
    [InlineData("' or 1=1--@example.com'")]
    public void TryParseRejectsInvalidAddresses(string? value)
    {
        Assert.False(EmailAddress.TryParse(value, out var address));
        Assert.Null(address);
    }

    /// <summary>
    /// Caracteres fora do ASCII podem colidir na normalização ("ſ".ToUpperInvariant() é "S"): um endereço
    /// diferente acharia a conta de outra pessoa (TM-67).
    /// </summary>
    [Theory]
    [InlineData("ſam@example.com")]
    [InlineData("sam@examplK.com")]
    [InlineData("josé@example.com")]
    [InlineData("ana@café.com")]
    [InlineData("ana@exa​mple.com")]
    public void TryParseRejectsNonAsciiAddresses(string value)
    {
        Assert.False(EmailAddress.TryParse(value, out _));
    }

    [Fact]
    public void TryParseAcceptsInternationalDomainsInPunycode()
    {
        Assert.True(EmailAddress.TryParse("ana@xn--caf-dma.com", out _));
    }

    [Fact]
    public void TryParseRejectsLocalPartAbove64Characters()
    {
        Assert.False(EmailAddress.TryParse(new string('a', 65) + "@example.com", out _));
    }
}
