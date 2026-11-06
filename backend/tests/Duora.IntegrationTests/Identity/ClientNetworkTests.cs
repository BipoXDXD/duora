using System.Net;
using Duora.Web.Security;

namespace Duora.IntegrationTests.Identity;

/// <summary>A chave da partição: é o que decide quais pedidos dividem a cota por IP.</summary>
public sealed class ClientNetworkTests
{
    [Theory]
    [InlineData("198.51.100.1", "198.51.100.1")]
    [InlineData("::ffff:198.51.100.1", "198.51.100.1")]
    [InlineData("2001:db8:a:b::1", "2001:db8:a:b::/64")]
    [InlineData("2001:db8:a:b:ffff:ffff:ffff:ffff", "2001:db8:a:b::/64")]
    [InlineData("2001:db8:a:c::", "2001:db8:a:c::/64")]
    [InlineData("::1", "::/64")]
    public void TheKeyIsTheAddressForIPv4AndTheSlash64ForIPv6(string address, string expected) =>
        Assert.Equal(expected, ClientNetwork.RateLimitKey(IPAddress.Parse(address)));

    [Fact]
    public void AnUnknownAddressHasItsOwnSharedKey() =>
        Assert.Equal("unknown", ClientNetwork.RateLimitKey(null));

    [Fact]
    public void TheLastAddressOfASlash64AndTheFirstOfTheNextHaveDifferentKeys() =>
        Assert.NotEqual(
            ClientNetwork.RateLimitKey(IPAddress.Parse("2001:db8:a:b:ffff:ffff:ffff:ffff")),
            ClientNetwork.RateLimitKey(IPAddress.Parse("2001:db8:a:c::")));
}
