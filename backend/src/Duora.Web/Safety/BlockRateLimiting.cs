using System.Threading.RateLimiting;
using Duora.Web.Security;
using Microsoft.Extensions.Options;

namespace Duora.Web.Safety;

/// <summary>
/// Limites do Safety na seção <c>RateLimiting</c> (spec 11.5; TM-26; decisão G6). Hipótese inicial: 30 bloqueios
/// por conta por hora. Em memória, como os de autenticação: valem para a única instância web (ADR-0009) e reiniciam
/// com o processo; nenhuma quota crítica depende deles.
/// </summary>
internal sealed class SafetyRateLimitOptions
{
    public const string Section = "RateLimiting";

    public WindowLimit BlockPerAccount { get; set; } = new() { PermitLimit = 30, Window = TimeSpan.FromHours(1) };
}

/// <summary>
/// Limite por conta autenticada, aplicado no handler: o middleware de rate limit roda antes da autenticação e não
/// enxerga quem é a pessoa. A chave é o id da conta, então trocar de sessão ou de IP não reinicia a contagem.
/// </summary>
internal sealed class BlockAttemptLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter;

    public BlockAttemptLimiter(IOptions<SafetyRateLimitOptions> options)
    {
        var limit = options.Value.BlockPerAccount;
        _limiter = PartitionedRateLimiter.Create<Guid, Guid>(account =>
            RateLimitPartition.GetFixedWindowLimiter(account, _ => limit.ToLimiterOptions()));
    }

    /// <summary>Consome uma tentativa; devolve o tempo de espera quando o limite estourou, ou null.</summary>
    public TimeSpan? TryConsume(Guid accountId)
    {
        using var lease = _limiter.AttemptAcquire(accountId);
        if (lease.IsAcquired)
        {
            return null;
        }

        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : TimeSpan.FromMinutes(1);
    }

    public void Dispose() => _limiter.Dispose();
}

internal static class BlockRateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddBlockRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<SafetyRateLimitOptions>()
            .BindConfiguration(SafetyRateLimitOptions.Section)
            .Validate(o => o.BlockPerAccount.IsValid(), "RateLimiting:BlockPerAccount precisa de PermitLimit >= 1 e janela positiva.")
            .ValidateOnStart();
        services.AddSingleton<BlockAttemptLimiter>();
        return services;
    }
}
