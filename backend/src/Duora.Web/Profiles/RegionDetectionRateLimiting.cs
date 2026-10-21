using System.Net;
using System.Threading.RateLimiting;
using Duora.Web.Security;
using Microsoft.Extensions.Options;

namespace Duora.Web.Profiles;

// Initial hypothesis: 6/minute/account, 60/minute/origin; tune before real activation.
internal sealed class RegionDetectionRateLimitOptions
{
    public const string Section = "RateLimiting";

    public WindowLimit RegionDetectionPerAccount { get; set; } = new() { PermitLimit = 6, Window = TimeSpan.FromMinutes(1) };

    public WindowLimit RegionDetectionPerIp { get; set; } = new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) };
}

/// <summary>
/// Conta e IP consumidos no handler, antes de ler o corpo: o middleware de rate limit roda antes da autenticação e não
/// sabe quem é a pessoa. Trocar de IP não zera a conta, e várias contas atrás do mesmo IP somam no IP.
/// </summary>
internal sealed class RegionDetectionLimiter : IDisposable
{
    private static readonly TimeSpan FallbackRetryAfter = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<Guid> _perAccount;
    private readonly PartitionedRateLimiter<string> _perIp;

    public RegionDetectionLimiter(IOptions<RegionDetectionRateLimitOptions> options)
    {
        var account = options.Value.RegionDetectionPerAccount;
        var ip = options.Value.RegionDetectionPerIp;
        _perAccount = PartitionedRateLimiter.Create<Guid, Guid>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => account.ToLimiterOptions()));
        _perIp = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => ip.ToLimiterOptions()));
    }

    /// <summary>Consome um envio da conta e do IP; devolve a espera quando algum dos dois estourou, ou null.</summary>
    public TimeSpan? TryConsume(Guid accountId, IPAddress? remoteIp)
    {
        using var byAccount = _perAccount.AttemptAcquire(accountId);
        using var byIp = _perIp.AttemptAcquire(ClientNetwork.RateLimitKey(remoteIp));
        if (byAccount.IsAcquired && byIp.IsAcquired)
        {
            return null;
        }

        return new[] { byAccount, byIp }
            .Where(lease => !lease.IsAcquired)
            .Select(lease => lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : FallbackRetryAfter)
            .Max();
    }

    public void Dispose()
    {
        _perAccount.Dispose();
        _perIp.Dispose();
    }
}

internal static class RegionDetectionRateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddRegionDetectionRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RegionDetectionRateLimitOptions>()
            .BindConfiguration(RegionDetectionRateLimitOptions.Section)
            .Validate(
                o => o.RegionDetectionPerAccount.IsValid() && o.RegionDetectionPerIp.IsValid(),
                "RateLimiting:RegionDetectionPerAccount e RegionDetectionPerIp precisam de PermitLimit >= 1 e janela positiva.")
            .ValidateOnStart();
        services.AddSingleton<RegionDetectionLimiter>();
        return services;
    }
}
