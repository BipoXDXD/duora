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

    /// <summary>
    /// Falhas da confirmação de e-mail (A03) pelo id de conta do corpo, exista a conta ou não: trocar de IP não renova
    /// a cota. Só a falha conta, depois de conferir o token: o id é público, e contar antes deixaria qualquer um
    /// esgotar a cota e travar o link válido do titular.
    /// </summary>
    public WindowLimit EmailConfirmationFailuresPerAccount { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromHours(1) };

    /// <summary>Falhas da conclusão do reset (A07), com a mesma regra e contador próprio.</summary>
    public WindowLimit PasswordResetFailuresPerAccount { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromHours(1) };

    /// <summary>
    /// Reautenticação, cadastro, ativação, novos códigos e desativação do MFA, e troca de senha (A11 a A16), por
    /// conta: abrir outra sessão não renova o limite (M1 da revisão do #38).
    /// </summary>
    public WindowLimit AccountSecurityPerAccount { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromHours(1) };

    /// <summary>
    /// Revogações que falham (A10: sessão inexistente, alheia ou já revogada), por conta. Revogar uma sessão própria
    /// e ativa não conta: quem roubou uma sessão não pode esgotar a cota e impedir o titular de derrubá-la.
    /// </summary>
    public WindowLimit SessionRevocationFailuresPerAccount { get; set; } = new() { PermitLimit = 30, Window = TimeSpan.FromHours(1) };

    /// <summary>
    /// Reautenticação, cadastro do MFA e novos códigos (A11, A12, A13 e A14), por IP, somados: várias contas atrás
    /// de um IP dividem esta cota, então abrir contas novas não multiplica as tentativas. Alta de propósito, para
    /// pegar só abuso em massa e não travar vizinhos de NAT/CGNAT; a força bruta numa conta é contida pela cota por
    /// conta e pelo bloqueio no banco. A remediação (revogar sessão, trocar senha, desativar MFA) fica fora.
    /// </summary>
    public WindowLimit AccountSecurityPerIp { get; set; } = new() { PermitLimit = 300, Window = TimeSpan.FromHours(1) };

    /// <summary>
    /// Credencial de captura da selfie (cada chamada faz um sts:AssumeRole), por conta: trocar de IP ou de sessão não
    /// renova a cota. Cabe com folga nas cinco tentativas por hora do serviço, com reabertura da câmera.
    /// </summary>
    public WindowLimit SelfieCapturePerAccount { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromHours(1) };

    public bool IsValid() =>
        new[]
        {
            SelfieCapturePerAccount,
            LoginPerIp, LoginPerAccount, RegisterPerIp, RegisterPerAccount,
            PasswordResetRequestPerIp, PasswordResetRequestPerAccount, TokenRedemptionPerIp,
            EmailConfirmationFailuresPerAccount, PasswordResetFailuresPerAccount,
            AccountSecurityPerAccount, SessionRevocationFailuresPerAccount, AccountSecurityPerIp,
        }.All(limit => limit.IsValid());
}

internal enum AccountOperation
{
    Login,
    Register,
    PasswordResetRequest,
}

/// <summary>Operações limitadas pelo id da conta, cada uma com o seu contador.</summary>
internal enum AccountIdOperation
{
    /// <summary>Reautenticação, MFA e troca de senha (A11 a A16).</summary>
    AccountSecurity,

    /// <summary>Revogação de sessão que falhou (A10).</summary>
    SessionRevocationFailure,

    /// <summary>Confirmação de e-mail que falhou (A03), pelo id do corpo.</summary>
    EmailConfirmationFailure,

    /// <summary>Conclusão do reset que falhou (A07), pelo id do corpo.</summary>
    PasswordResetFailure,

    /// <summary>Credencial de captura da selfie (um sts:AssumeRole por chamada).</summary>
    SelfieCapture,
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

/// <summary>
/// Limite pelo id da conta (spec 11.5), um contador por <see cref="AccountIdOperation"/>. Nas rotas com sessão é um
/// filtro de endpoint, porque o middleware de rate limit roda antes da autenticação e não conhece a conta; nos
/// resgates por token e na revogação de sessão, o handler consome só quando a operação falha.
/// </summary>
internal sealed class AccountSecurityAttemptLimiter : IDisposable
{
    private readonly Dictionary<AccountIdOperation, PartitionedRateLimiter<Guid>> _limiters;

    public AccountSecurityAttemptLimiter(IOptions<AuthRateLimitOptions> options)
    {
        var limits = options.Value;
        _limiters = new()
        {
            [AccountIdOperation.AccountSecurity] = Create(limits.AccountSecurityPerAccount),
            [AccountIdOperation.SessionRevocationFailure] = Create(limits.SessionRevocationFailuresPerAccount),
            [AccountIdOperation.EmailConfirmationFailure] = Create(limits.EmailConfirmationFailuresPerAccount),
            [AccountIdOperation.PasswordResetFailure] = Create(limits.PasswordResetFailuresPerAccount),
            [AccountIdOperation.SelfieCapture] = Create(limits.SelfieCapturePerAccount),
        };
    }

    /// <summary>Consome uma tentativa da conta; devolve o tempo de espera quando o limite estourou, ou null.</summary>
    public TimeSpan? TryConsume(AccountIdOperation operation, Guid userId)
    {
        using var lease = _limiters[operation].AttemptAcquire(userId);
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

    private static PartitionedRateLimiter<Guid> Create(WindowLimit limit) =>
        PartitionedRateLimiter.Create<Guid, Guid>(account =>
            RateLimitPartition.GetFixedWindowLimiter(account, _ => limit.ToLimiterOptions()));
}

internal static class AccountSecurityRateLimiting
{
    /// <summary>
    /// 429 com <c>Retry-After</c> quando a conta passou do limite das operações de segurança da conta
    /// (<see cref="AuthRateLimitOptions.AccountSecurityPerAccount"/>), de qualquer sessão ou IP.
    /// </summary>
    public static TBuilder RequireAccountRateLimit<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAccountRateLimit(AccountIdOperation.AccountSecurity);

    /// <summary>429 com <c>Retry-After</c> quando a conta passou do limite da operação, de qualquer sessão ou IP.</summary>
    public static TBuilder RequireAccountRateLimit<TBuilder>(this TBuilder builder, AccountIdOperation operation)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter((context, next) =>
        {
            var http = context.HttpContext;
            var userId = Duora.Web.Identity.DuoraClaims.ReadUserId(http.User)
                ?? throw new InvalidOperationException("Limite por conta numa rota sem sessão.");
            var limiter = http.RequestServices.GetRequiredService<AccountSecurityAttemptLimiter>();
            return limiter.TryConsume(operation, userId) is { } retryAfter
                ? ValueTask.FromResult<object?>(ApiProblems.TooManyRequests(http, retryAfter))
                : next(context);
        });

    /// <summary>
    /// Soma a rota à cota por IP das operações de segurança que não são remediação
    /// (<see cref="AuthRateLimitOptions.AccountSecurityPerIp"/>), aplicada antes da autenticação.
    /// </summary>
    public static TBuilder RequireAccountSecurityAddressLimit<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireRateLimiting(AuthRateLimitPolicies.AccountSecurity);
}

internal static class AuthRateLimitPolicies
{
    public const string Login = "auth-login";
    public const string Register = "auth-register";
    public const string PasswordResetRequest = "auth-password-reset-request";
    public const string TokenRedemption = "auth-token-redemption";
    public const string AccountSecurity = "auth-account-security";

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<AuthRateLimitOptions>()
            .BindConfiguration(AuthRateLimitOptions.Section)
            .Validate(o => o.IsValid(), "RateLimiting: todo limite precisa de PermitLimit >= 1 e janela positiva.")
            .ValidateOnStart();
        services.AddSingleton<AccountAttemptLimiter>();
        services.AddSingleton<AccountSecurityAttemptLimiter>();

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
                AddPerIpPolicy(rateLimiter, AccountSecurity, limits.AccountSecurityPerIp);
            });

        return services;
    }

    private static void AddPerIpPolicy(
        Microsoft.AspNetCore.RateLimiting.RateLimiterOptions rateLimiter,
        string policy,
        WindowLimit limit) =>
        rateLimiter.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
            // Atrás do proxy, o IP só é o do cliente com Forwarded Headers configurado (pendência de deploy).
            ClientNetwork.RateLimitKey(context.Connection.RemoteIpAddress),
            _ => limit.ToLimiterOptions()));

    private static async ValueTask WriteRejectionAsync(
        Microsoft.AspNetCore.RateLimiting.OnRejectedContext rejected,
        CancellationToken cancellationToken)
    {
        var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromMinutes(1);
        await ApiProblems.TooManyRequests(rejected.HttpContext, retryAfter).ExecuteAsync(rejected.HttpContext);
    }
}
