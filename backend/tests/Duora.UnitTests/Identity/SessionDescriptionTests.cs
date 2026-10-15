using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

public sealed class SessionDescriptionTests
{
    [Theory]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36",
        "Chrome em Windows")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36 Edg/141.0",
        "Edge em Windows")]
    [InlineData(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15",
        "Safari em macOS")]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 26_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Mobile/15E148 Safari/604.1",
        "Safari em iOS")]
    [InlineData(
        "Mozilla/5.0 (X11; Linux x86_64; rv:144.0) Gecko/20100101 Firefox/144.0",
        "Firefox em Linux")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 16) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Mobile Safari/537.36",
        "Chrome em Android")]
    public void DescribesBrowserAndSystemOnly(string userAgent, string expected)
    {
        Assert.Equal(expected, SessionDescription.FromUserAgent(userAgent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curl/8.9.1")]
    public void UnknownAgentsGetAGenericDescription(string? userAgent)
    {
        Assert.Equal("Dispositivo desconhecido", SessionDescription.FromUserAgent(userAgent));
    }

    [Fact]
    public void NeverEchoesArbitraryUserAgentText()
    {
        var description = SessionDescription.FromUserAgent("CANARY-7f3e <script>alert(1)</script> Firefox/1.0");

        Assert.DoesNotContain("CANARY", description, StringComparison.Ordinal);
        Assert.DoesNotContain("<", description, StringComparison.Ordinal);
    }
}
