using System.Threading.RateLimiting;
using Duora.Core.Identity;
using Duora.Web.Http;
using Microsoft.Extensions.Options;

namespace Duora.Web.Security;

/// <summary>Limite de uma operação: quantas tentativas cabem numa janela fixa.</summary>
internal sealed class WindowLimit
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }

    public bool IsValid() => PermitLimit >= 1 && Window > TimeSpan.Zero;

    public FixedWindowRateLimiterOptions ToLimiterOptions() =>
        new() { PermitLimit = PermitLimit, Window = Window, QueueLimit = 0, AutoReplenishment = true };
}

/// <summary>
/// Limites das rotas de autenticação (spec 11.5), por IP e por conta-alvo. Valores iniciais, versionados aqui e
/// ajustáveis na seção <c>RateLimiting</c>. Em memória: valem para a única instância web do piloto (ADR-0009) e
/// reiniciam com o processo; o bloqueio do Identity, no banco, é o freio que sobrevive ao restart.
/// </summary>
internal sealed class AuthRateLimitOptions
{
    public const string Section = "RateLimiting";

    public WindowLimit LoginPerIp { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    public WindowLimit LoginPerAccount { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(15) };

    public WindowLimit RegisterPerIp { get; set; } = new() { PermitLimit = 20, Window = TimeSpan.FromHours(1) };

    public WindowLimit RegisterPerAccount { get; set; } = new() { PermitLimit = 3, Window = TimeSpan.FromHours(1) };

    public WindowLimit PasswordResetRequestPerIp { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromHours(1) };

    public WindowLimit PasswordResetRequestPerAccount { get; set; } = new() { PermitLimit = 3, Window = TimeSpan.FromHours(1) };

    /// <summary>Confirmação de e-mail e troca de senha por token: o token não é adivinhável, o limite só segura abuso.</summary>
    public WindowLimit TokenRedemptionPerIp { get; set; } = new() { PermitLimit = 20, Window = TimeSpan.FromMinutes(10) };

    public bool IsValid() =>
        new[]
        {
            LoginPerIp, LoginPerAccount, RegisterPerIp, RegisterPerAccount,
            PasswordResetRequestPerIp, PasswordResetRequestPerAccount, TokenRedemptionPerIp,
        }.All(limit => limit.IsValid());
}

internal enum AccountOperation
{
    Login,
    Register,
    PasswordResetRequest,
}

/// <summary>
/// Limite por conta-alvo, aplicado depois de ler o corpo (o middleware de rate limit só enxerga a requisição antes
/// disso). A chave é o e-mail normalizado, exista a conta ou não: o 429 aparece igual para as duas, sem revelar
/// cadastro. Trade-off aceito: quem insiste no e-mail de outra pessoa também atrasa o login dela (ADR-0019).
/// </summary>
internal sealed class AccountAttemptLimiter : IDisposable
{
    private readonly Dictionary<AccountOperation, PartitionedRateLimiter<string>> _limiters;

    public AccountAttemptLimiter(IOptions<AuthRateLimitOptions> options)
    {
        var limits = options.Value;
        _limiters = new()
        {
            [AccountOperation.Login] = Create(limits.LoginPerAccount),
            [AccountOperation.Register] = Create(limits.RegisterPerAccount),
            [AccountOperation.PasswordResetRequest] = Create(limits.PasswordResetRequestPerAccount),
        };
    }

    /// <summary>Consome uma tentativa; devolve o tempo de espera quando o limite estourou, ou null.</summary>
    public TimeSpan? TryConsume(AccountOperation operation, EmailAddress account)
    {
        using var lease = _limiters[operation].AttemptAcquire(account.Value.ToUpperInvariant());
        if (lease.IsAcquired)
        {
            return null;
        }

        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : TimeSpan.FromMinutes(1);
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }

    private static PartitionedRateLimiter<string> Create(WindowLimit limit) =>
        PartitionedRateLimiter.Create<string, string>(account =>
            RateLimitPartition.GetFixedWindowLimiter(account, _ => limit.ToLimiterOptions()));
}

internal static class AuthRateLimitPolicies
{
    public const string Login = "auth-login";
    public const string Register = "auth-register";
    public const string PasswordResetRequest = "auth-password-reset-request";
    public const string TokenRedemption = "auth-token-redemption";

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<AuthRateLimitOptions>()
            .BindConfiguration(AuthRateLimitOptions.Section)
            .Validate(o => o.IsValid(), "RateLimiting: todo limite precisa de PermitLimit >= 1 e janela positiva.")
            .ValidateOnStart();
        services.AddSingleton<AccountAttemptLimiter>();

        services.AddRateLimiter(_ => { });
        services.AddOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>()
            .Configure<IOptions<AuthRateLimitOptions>>((rateLimiter, configured) =>
            {
                var limits = configured.Value;
                rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                rateLimiter.OnRejected = WriteRejectionAsync;
                AddPerIpPolicy(rateLimiter, Login, limits.LoginPerIp);
                AddPerIpPolicy(rateLimiter, Register, limits.RegisterPerIp);
                AddPerIpPolicy(rateLimiter, PasswordResetRequest, limits.PasswordResetRequestPerIp);
                AddPerIpPolicy(rateLimiter, TokenRedemption, limits.TokenRedemptionPerIp);
            });

        return services;
    }

    private static void AddPerIpPolicy(
        Microsoft.AspNetCore.RateLimiting.RateLimiterOptions rateLimiter,
        string policy,
        WindowLimit limit) =>
        rateLimiter.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
            // Atrás do proxy, o IP só é o do cliente com Forwarded Headers configurado (pendência de deploy).
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => limit.ToLimiterOptions()));

    private static async ValueTask WriteRejectionAsync(
        Microsoft.AspNetCore.RateLimiting.OnRejectedContext rejected,
        CancellationToken cancellationToken)
    {
        var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromMinutes(1);
        await ApiProblems.TooManyRequests(rejected.HttpContext, retryAfter).ExecuteAsync(rejected.HttpContext);
    }
}
