using System.Net;
using System.Threading.RateLimiting;
using Duora.Web.Security;
using Microsoft.Extensions.Options;

namespace Duora.Web.Media;

/// <summary>
/// Limites do envio de fotos na seção <c>RateLimiting</c> (spec 11.5, TM-43). Hipótese inicial: 20 envios por hora por
/// conta e por IP, a medir. Em memória, como os demais: valem para a única instância web (ADR-0009) e reiniciam com o
/// processo; o teto de pendentes no banco é a trava que sobrevive ao restart e vale entre réplicas.
/// </summary>
internal sealed class PhotoUploadRateLimitOptions
{
    public const string Section = "RateLimiting";

    public WindowLimit PhotoUploadPerAccount { get; set; } = new() { PermitLimit = 20, Window = TimeSpan.FromHours(1) };

    public WindowLimit PhotoUploadPerIp { get; set; } = new() { PermitLimit = 20, Window = TimeSpan.FromHours(1) };
}

/// <summary>
/// Conta e IP consumidos no handler, antes de ler o corpo: o middleware de rate limit roda antes da autenticação e não
/// sabe quem é a pessoa. Trocar de IP não zera a conta, e várias contas atrás do mesmo IP somam no IP.
/// </summary>
internal sealed class PhotoUploadLimiter : IDisposable
{
    private static readonly TimeSpan FallbackRetryAfter = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<Guid> _perAccount;
    private readonly PartitionedRateLimiter<string> _perIp;

    public PhotoUploadLimiter(IOptions<PhotoUploadRateLimitOptions> options)
    {
        var account = options.Value.PhotoUploadPerAccount;
        var ip = options.Value.PhotoUploadPerIp;
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

internal static class PhotoUploadRateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddPhotoUploadRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<PhotoUploadRateLimitOptions>()
            .BindConfiguration(PhotoUploadRateLimitOptions.Section)
            .Validate(
                o => o.PhotoUploadPerAccount.IsValid() && o.PhotoUploadPerIp.IsValid(),
                "RateLimiting:PhotoUploadPerAccount e PhotoUploadPerIp precisam de PermitLimit >= 1 e janela positiva.")
            .ValidateOnStart();
        services.AddSingleton<PhotoUploadLimiter>();
        return services;
    }
}
