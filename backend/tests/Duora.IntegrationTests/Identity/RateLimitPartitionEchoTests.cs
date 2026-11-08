using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Identity;

/// <summary>O eco de teste ocupa o <c>GlobalLimiter</c>; ele não pode tomar o lugar de um limitador da aplicação.</summary>
public sealed class RateLimitPartitionEchoTests
{
    [Fact]
    public void AnApplicationGlobalLimiterMakesTheEchoFailLoudly()
    {
        var services = new ServiceCollection();
        services.AddOptions<RateLimiterOptions>().Configure(options =>
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                _ => RateLimitPartition.GetNoLimiter("aplicacao")));
        RateLimitPartitionEcho.Register(services);
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value);

        Assert.Contains("GlobalLimiter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnApplicationGlobalLimiterTheEchoInstallsItsOwn()
    {
        var services = new ServiceCollection();
        RateLimitPartitionEcho.Register(services);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter);
    }
}
