using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Duora.Web.Security;

/// <summary>
/// Redes de onde vêm os proxies confiáveis (seção <c>ReverseProxy</c>). Em produção, a rede <c>edge</c> do Caddy
/// (docs/operations/deploy.md). Vazio por padrão: sem configuração, nenhum <c>X-Forwarded-*</c> é aceito.
/// </summary>
internal sealed class ReverseProxyOptions
{
    public const string Section = "ReverseProxy";

    public List<string> KnownNetworks { get; set; } = [];

    public bool IsValid() => KnownNetworks.TrueForAll(network => System.Net.IPNetwork.TryParse(network, out _));
}

internal static class ReverseProxy
{
    /// <summary>
    /// Aceita <c>X-Forwarded-For</c> e <c>X-Forwarded-Proto</c> só de remetente dentro das redes configuradas e só
    /// um salto (o Caddy), para o IP do cliente que o rate limit usa não ser escolhido por quem envia o header
    /// (spec 11.3). Os padrões do framework (loopback confiável) são descartados.
    /// </summary>
    public static IServiceCollection AddTrustedReverseProxy(this IServiceCollection services)
    {
        services.AddOptions<ReverseProxyOptions>()
            .BindConfiguration(ReverseProxyOptions.Section)
            .Validate(o => o.IsValid(), "ReverseProxy:KnownNetworks precisa listar redes em notação CIDR (ex.: 172.30.10.0/24).")
            .ValidateOnStart();

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ReverseProxyOptions>>((forwarded, proxy) =>
            {
                // Com as duas listas vazias o middleware aceitaria os headers de QUALQUER remetente; por isso, sem rede
                // configurada, o processamento fica desligado em vez de só ter a lista limpa.
                forwarded.ForwardedHeaders = proxy.Value.KnownNetworks.Count == 0
                    ? ForwardedHeaders.None
                    : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.ForwardLimit = 1;
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                foreach (var network in proxy.Value.KnownNetworks)
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            });

        return services;
    }
}
