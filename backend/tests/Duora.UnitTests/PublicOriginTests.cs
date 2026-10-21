using Duora.Core;

namespace Duora.UnitTests;

public sealed class PublicOriginTests
{
    [Theory]
    [InlineData("https://duora.example", "https://duora.example")]
    [InlineData("https://duora.example/", "https://duora.example")]
    [InlineData("https://localhost:5173", "https://localhost:5173")]
    [InlineData("HTTPS://Duora.Example", "https://duora.example")]
    public void ParsesPlainHttpsOriginsIntoTheSerializedOriginForm(string value, string expected)
    {
        Assert.True(PublicOrigin.TryParse(value, out var origin));
        Assert.Equal(expected, origin!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("duora.example")]
    [InlineData("http://duora.example")]
    [InlineData("https://duora.example/app")]
    [InlineData("https://duora.example?x=1")]
    [InlineData("https://duora.example#x")]
    [InlineData("https://user@duora.example")]
    public void RejectsAnythingButAPlainHttpsOrigin(string? value)
    {
        Assert.False(PublicOrigin.TryParse(value, out var origin));
        Assert.Null(origin);
    }

    [Fact]
    public void ResolvesPathsAgainstTheOrigin()
    {
        Assert.True(PublicOrigin.TryParse("https://duora.example", out var origin));

        Assert.Equal(new Uri("https://duora.example/confirm-email?a=1"), origin.Resolve("/confirm-email?a=1"));
    }
}
