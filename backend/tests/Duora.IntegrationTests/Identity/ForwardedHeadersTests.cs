using System.Net;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// O rate limit por IP precisa do IP do cliente. Atrás do proxy, ele vem de <c>X-Forwarded-For</c>, mas só quando
/// quem manda é o proxy confiável; de qualquer outro remetente o header é ignorado, senão cada requisição
/// escolheria a própria partição. Sem configuração, nenhum proxy é confiável.
/// </summary>
public sealed class ForwardedHeadersTests(PostgresFixture postgres)
{
    private const string ProxyNetwork = "172.30.10.0/24";
    private const string Proxy = "172.30.10.5";
    private const string Stranger = "203.0.113.9";

    [Fact]
    public async Task ForwardedForFromAnUntrustedSenderIsIgnored()
    {
        await using var factory = Factory(trustProxyNetwork: true);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var first = await LoginAsync(browser, from: Stranger, forwardedFor: "198.51.100.1");
        using var second = await LoginAsync(browser, from: Stranger, forwardedFor: "198.51.100.2");
        using var third = await LoginAsync(browser, from: Stranger, forwardedFor: "198.51.100.3");

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await ProblemAssert.IsProblemAsync(third, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    [Fact]
    public async Task ForwardedForFromTheTrustedProxyPartitionsByClient()
    {
        await using var factory = Factory(trustProxyNetwork: true);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var firstA = await LoginAsync(browser, from: Proxy, forwardedFor: "198.51.100.1");
        using var secondA = await LoginAsync(browser, from: Proxy, forwardedFor: "198.51.100.1");
        using var firstB = await LoginAsync(browser, from: Proxy, forwardedFor: "198.51.100.2");
        using var thirdA = await LoginAsync(browser, from: Proxy, forwardedFor: "198.51.100.1");

        Assert.Equal(HttpStatusCode.Unauthorized, firstB.StatusCode);
        await ProblemAssert.IsProblemAsync(thirdA, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    [Fact]
    public async Task WithoutConfigurationNoProxyIsTrusted()
    {
        await using var factory = Factory(trustProxyNetwork: false);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var first = await LoginAsync(browser, from: Proxy, forwardedFor: "198.51.100.1");
        using var second = await LoginAsync(browser, from: Proxy, forwardedFor: "198.51.100.2");
        using var third = await LoginAsync(browser, from: Proxy, forwardedFor: "198.51.100.3");

        await ProblemAssert.IsProblemAsync(third, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    /// <summary>
    /// Um cliente IPv6 costuma ter um /64 inteiro: contar por endereço daria a ele cotas sem fim. Endereços do mesmo
    /// /64 dividem a cota; outro /64 tem a sua.
    /// </summary>
    [Fact]
    public async Task IPv6AddressesInTheSameSlash64ShareTheLoginLimit()
    {
        await using var factory = Factory(trustProxyNetwork: false);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var first = await LoginFromAsync(browser, "2001:db8:a:b::1");
        using var second = await LoginFromAsync(browser, "2001:db8:a:b:ffff:ffff:ffff:fffe");
        using var third = await LoginFromAsync(browser, "2001:db8:a:b::2");
        using var otherNetwork = await LoginFromAsync(browser, "2001:db8:a:c::1");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await ProblemAssert.IsProblemAsync(third, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.Equal(HttpStatusCode.Unauthorized, otherNetwork.StatusCode);
    }

    private static Task<HttpResponseMessage> LoginFromAsync(Browser browser, string from) =>
        browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = IdentityScenario.NewEmail(), password = "senha que nao confere com nada" },
            new MutationHeaders(Extra: new Dictionary<string, string> { [RemoteAddressFromTestHeader.Header] = from }));

    private DuoraWebFactory Factory(bool trustProxyNetwork)
    {
        var settings = new Dictionary<string, string?> { ["RateLimiting:LoginPerIp:PermitLimit"] = "2" };
        if (trustProxyNetwork)
        {
            settings["ReverseProxy:KnownNetworks:0"] = ProxyNetwork;
        }

        return new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = settings,
            TestServices = RemoteAddressFromTestHeader.Register,
        };
    }

    private static Task<HttpResponseMessage> LoginAsync(Browser browser, string from, string forwardedFor) =>
        browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = IdentityScenario.NewEmail(), password = "senha que nao confere com nada" },
            new MutationHeaders(Extra: new Dictionary<string, string>
            {
                [RemoteAddressFromTestHeader.Header] = from,
                ["X-Forwarded-For"] = forwardedFor,
            }));
}
