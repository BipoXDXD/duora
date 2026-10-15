using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
            TestServices = services => services.AddTransient<IStartupFilter, RemoteAddressFromTestHeader>(),
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

    /// <summary>
    /// O TestServer não tem conexão de rede; este filtro, só de teste, põe o IP do remetente antes de todo o pipeline.
    /// </summary>
    private sealed class RemoteAddressFromTestHeader : IStartupFilter
    {
        public const string Header = "X-Test-Remote-Address";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[Header].ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
