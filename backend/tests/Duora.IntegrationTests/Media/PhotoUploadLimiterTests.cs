using System.Net;
using Duora.Web.Media;
using Duora.Web.Security;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// O limite de envio de fotos por IP usa a mesma chave de origem das rotas de autenticação: IPv6 conta pelo /64, que
/// um cliente costuma ter inteiro.
/// </summary>
public sealed class PhotoUploadLimiterTests
{
    [Fact]
    public void IPv6AddressesInTheSameSlash64ShareThePerIpLimit()
    {
        using var limiter = LimiterWithOneUploadPerIp();

        var first = limiter.TryConsume(Guid.NewGuid(), IPAddress.Parse("2001:db8:a:b::1"));
        var sameNetwork = limiter.TryConsume(Guid.NewGuid(), IPAddress.Parse("2001:db8:a:b:ffff::2"));
        var otherNetwork = limiter.TryConsume(Guid.NewGuid(), IPAddress.Parse("2001:db8:a:c::1"));

        Assert.Null(first);
        Assert.NotNull(sameNetwork);
        Assert.Null(otherNetwork);
    }

    private static PhotoUploadLimiter LimiterWithOneUploadPerIp() =>
        new(Options.Create(new PhotoUploadRateLimitOptions
        {
            PhotoUploadPerAccount = new WindowLimit { PermitLimit = 10, Window = TimeSpan.FromHours(1) },
            PhotoUploadPerIp = new WindowLimit { PermitLimit = 1, Window = TimeSpan.FromHours(1) },
        }));
}
