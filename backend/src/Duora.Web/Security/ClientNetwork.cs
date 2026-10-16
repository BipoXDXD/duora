using System.Net;
using System.Net.Sockets;

namespace Duora.Web.Security;

/// <summary>
/// Chave do rate limit por origem. IPv4 conta por endereço. IPv6 conta pelo prefixo /64, porque um cliente recebe
/// em geral um /64 inteiro e trocaria de endereço a cada pedido para ter cota nova. IPv4 mapeado em IPv6 conta como
/// IPv4.
/// </summary>
internal static class ClientNetwork
{
    private const int Slash64Bytes = 8;

    public static string RateLimitKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, Slash64Bytes, bytes.Length - Slash64Bytes);
        return $"{new IPAddress(bytes)}/64";
    }
}
