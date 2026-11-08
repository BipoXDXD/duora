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
    private const string OtherProxy = "172.30.10.7";
    private const string Stranger = "203.0.113.9";
    private const string ClientA = "198.51.100.1";
    private const string ClientB = "198.51.100.2";
    private const string Forged = "203.0.113.66";

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

    /// <summary>
    /// Fronteira da cota com o <c>X-Forwarded-For</c> de um valor só que o Caddy entrega ao web
    /// (scripts/deploy/test_caddy_client_ip.py): cada cliente tem exatamente o limite, e esgotar o de um não toca o do outro.
    /// </summary>
    [Fact]
    public async Task EachClientBehindTheProxyGetsExactlyItsOwnLimit()
    {
        await using var factory = Factory(trustProxyNetwork: true);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var firstA = await LoginAsync(browser, from: Proxy, forwardedFor: ClientA);
        using var lastAllowedA = await LoginAsync(browser, from: Proxy, forwardedFor: ClientA);
        using var overLimitA = await LoginAsync(browser, from: Proxy, forwardedFor: ClientA);
        using var firstB = await LoginAsync(browser, from: Proxy, forwardedFor: ClientB);
        using var lastAllowedB = await LoginAsync(browser, from: Proxy, forwardedFor: ClientB);
        using var overLimitB = await LoginAsync(browser, from: Proxy, forwardedFor: ClientB);

        Assert.Equal(HttpStatusCode.Unauthorized, firstA.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, lastAllowedA.StatusCode);
        await ProblemAssert.IsProblemAsync(overLimitA, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.Equal(HttpStatusCode.Unauthorized, firstB.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, lastAllowedB.StatusCode);
        await ProblemAssert.IsProblemAsync(overLimitB, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    [Fact]
    public async Task IPv6ClientsBehindTheProxyShareTheLimitOfTheirSlash64()
    {
        await using var factory = Factory(trustProxyNetwork: true);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var first = await LoginAsync(browser, from: Proxy, forwardedFor: "2001:db8:a:b::1");
        using var second = await LoginAsync(browser, from: Proxy, forwardedFor: "2001:db8:a:b:ffff:ffff:ffff:fffe");
        using var third = await LoginAsync(browser, from: Proxy, forwardedFor: "2001:db8:a:b::2");
        using var otherNetwork = await LoginAsync(browser, from: Proxy, forwardedFor: "2001:db8:a:c::1");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await ProblemAssert.IsProblemAsync(third, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.Equal(HttpStatusCode.Unauthorized, otherNetwork.StatusCode);
    }

    [Fact]
    public async Task AnIPv4ClientAndItsMappedFormBehindTheProxyShareTheLimit()
    {
        await using var factory = Factory(trustProxyNetwork: true);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var first = await LoginAsync(browser, from: Proxy, forwardedFor: ClientA);
        using var second = await LoginAsync(browser, from: Proxy, forwardedFor: $"::ffff:{ClientA}");
        using var third = await LoginAsync(browser, from: Proxy, forwardedFor: ClientA);

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await ProblemAssert.IsProblemAsync(third, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    /// <summary>
    /// O Kestrel em socket dual-stack entrega o remetente IPv4 como <c>::ffff:a.b.c.d</c>. Por esse remetente também só
    /// o valor mais à direita vira o cliente: nem o proxy, em nenhuma das formas, nem o forjado à esquerda.
    /// </summary>
    [Fact]
    public async Task TheTrustedProxyInItsMappedFormStillPartitionsByClient()
    {
        await using var factory = Factory(trustProxyNetwork: true);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        var mappedProxy = $"::ffff:{Proxy}";

        using var firstA = await LoginAsync(browser, from: mappedProxy, forwardedFor: $"{Forged}, {ClientA}");
        using var secondA = await LoginAsync(browser, from: mappedProxy, forwardedFor: $"{Forged}, {ClientA}");
        using var firstB = await LoginAsync(browser, from: mappedProxy, forwardedFor: $"{Forged}, {ClientB}");
        using var thirdA = await LoginAsync(browser, from: mappedProxy, forwardedFor: $"{Forged}, {ClientA}");

        Assert.Equal(
            new[] { ClientA, ClientA, ClientB, ClientA },
            new[] { firstA, secondA, firstB, thirdA }.Select(ResolvedRemoteAddressEcho.Of));
        Assert.Equal(HttpStatusCode.Unauthorized, firstA.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, secondA.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, firstB.StatusCode);
        await ProblemAssert.IsProblemAsync(thirdA, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    /// <summary>
    /// O Caddy reescreve o header, mas o web não depende disso: de uma lista, só o valor mais à direita conta, e um
    /// valor dentro da rede do proxy não abre um segundo salto. Com <c>ForwardLimit</c> maior, a esquerda forjada viraria
    /// a partição.
    /// </summary>
    [Fact]
    public async Task OnlyTheRightmostForwardedValueCountsEvenInsideTheProxyNetwork()
    {
        await using var factory = Factory(trustProxyNetwork: true);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var first = await LoginAsync(browser, from: Proxy, forwardedFor: $"198.51.100.1, {OtherProxy}");
        using var second = await LoginAsync(browser, from: Proxy, forwardedFor: $"198.51.100.2, {OtherProxy}");
        using var third = await LoginAsync(browser, from: Proxy, forwardedFor: $"198.51.100.3, {OtherProxy}");

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await ProblemAssert.IsProblemAsync(third, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    /// <summary>Só o Caddy lê <c>Cf-Connecting-IP</c>: para o web ele é um header qualquer, sem efeito na partição.</summary>
    [Fact]
    public async Task ACloudflareHeaderThatReachesTheWebDoesNotChooseThePartition()
    {
        await using var factory = Factory(trustProxyNetwork: true);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var first = await LoginWithCloudflareHeaderAsync(browser, cloudflareClient: "203.0.113.1");
        using var second = await LoginWithCloudflareHeaderAsync(browser, cloudflareClient: "203.0.113.2");
        using var third = await LoginWithCloudflareHeaderAsync(browser, cloudflareClient: "203.0.113.3");

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await ProblemAssert.IsProblemAsync(third, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    /// <summary>
    /// Sem endereço válido não há partição nova: tudo cai na do proxy, não numa partição sem endereço que juntaria todo
    /// header quebrado, e o lixo não vira erro 500.
    /// </summary>
    [Fact]
    public async Task AnUnparsableForwardedForFallsBackToTheProxyPartition()
    {
        await using var factory = Factory(trustProxyNetwork: true);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();

        using var first = await LoginAsync(browser, from: Proxy, forwardedFor: "not-an-ip");
        using var second = await LoginAsync(browser, from: Proxy, forwardedFor: "still-not-an-ip");
        using var third = await LoginAsync(browser, from: Proxy, forwardedFor: "1.2.3.4.5");

        Assert.Equal(new[] { Proxy, Proxy, Proxy }, new[] { first, second, third }.Select(ResolvedRemoteAddressEcho.Of));
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await ProblemAssert.IsProblemAsync(third, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    private static Task<HttpResponseMessage> LoginWithCloudflareHeaderAsync(Browser browser, string cloudflareClient) =>
        browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = IdentityScenario.NewEmail(), password = "senha que nao confere com nada" },
            new MutationHeaders(Extra: new Dictionary<string, string>
            {
                [RemoteAddressFromTestHeader.Header] = Proxy,
                ["X-Forwarded-For"] = ClientA,
                ["Cf-Connecting-IP"] = cloudflareClient,
            }));

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
            TestServices = services =>
            {
                RemoteAddressFromTestHeader.Register(services);
                ResolvedRemoteAddressEcho.Register(services);
            },
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
