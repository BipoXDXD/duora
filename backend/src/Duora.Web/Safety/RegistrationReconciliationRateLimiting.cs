using System.Threading.RateLimiting;
using Duora.Web.Security;
using Microsoft.Extensions.Options;

namespace Duora.Web.Safety;

/// <summary>
/// Limite da prévia da reconciliação (API-ADM25, ADR-0048) na seção <c>RateLimiting</c>: 6 prévias por minuto por
/// conta moderadora, hipótese conservadora para uma consulta agregada de até 10 s. Em memória, como os demais
/// limites: vale para a única instância web (ADR-0009) e reinicia com o processo. Com mais de uma réplica, cada uma
/// teria a própria contagem; o estado compartilhado fica pendente até existir segunda instância.
/// </summary>
internal sealed class RegistrationReconciliationRateLimitOptions
{
    public const string Section = "RateLimiting";

    public WindowLimit ReconciliationPreviewPerModerator { get; set; } =
        new() { PermitLimit = 6, Window = TimeSpan.FromMinutes(1) };
}

/// <summary>
/// Limite por conta autenticada, aplicado depois da autorização: o middleware de rate limit roda antes da autenticação
/// e não enxerga quem é a pessoa. A chave é o id da conta, então trocar de sessão ou de IP não reinicia a contagem.
/// Só conta em memória; nada é gravado no banco.
/// </summary>
internal sealed class ReconciliationPreviewLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter;

    public ReconciliationPreviewLimiter(IOptions<RegistrationReconciliationRateLimitOptions> options)
    {
        var limit = options.Value.ReconciliationPreviewPerModerator;
        _limiter = PartitionedRateLimiter.Create<Guid, Guid>(account =>
            RateLimitPartition.GetFixedWindowLimiter(account, _ => limit.ToLimiterOptions()));
    }

    /// <summary>Consome uma prévia; devolve o tempo de espera quando o limite estourou, ou null.</summary>
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

internal static class RegistrationReconciliationRateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddRegistrationReconciliationRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RegistrationReconciliationRateLimitOptions>()
            .BindConfiguration(RegistrationReconciliationRateLimitOptions.Section)
            .Validate(o => o.ReconciliationPreviewPerModerator.IsValid(),
                "RateLimiting:ReconciliationPreviewPerModerator precisa de PermitLimit >= 1 e janela positiva.")
            .ValidateOnStart();
        services.AddSingleton<ReconciliationPreviewLimiter>();
        return services;
    }
}
