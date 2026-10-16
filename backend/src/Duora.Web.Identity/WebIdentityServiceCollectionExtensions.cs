using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Duora.Web.Identity;

/// <summary>Nomes do esquema de autenticação por cookie (ADR-0005).</summary>
public static class DuoraAuthentication
{
    public const string Scheme = "Duora.Session";

    /// <summary>
    /// Esquema usado só para gravar a sessão nova no login, com o mesmo cookie e o mesmo formato de ticket do
    /// <see cref="Scheme"/>. O handler do <see cref="Scheme"/>, que já autenticou a requisição, reaproveitaria a
    /// chave anterior no SignIn (RenewAsync em vez de StoreAsync). Este handler também lê o cookie da requisição, por
    /// isso ele sozinho não basta: quem garante que a chave anterior não sobrevive é a ordem no login, com
    /// <c>SignOutAsync(Scheme)</c> revogando a sessão anterior no banco ANTES do <c>SignInAsync(SignInScheme)</c>.
    /// Invariante: nunca chamar SignIn sem esse SignOut antes (coberta pelos testes de fixation).
    /// </summary>
    public const string SignInScheme = "Duora.Session.SignIn";

    /// <summary>
    /// Prefixo <c>__Host-</c>: o navegador só aceita o cookie com <c>Secure</c>, <c>Path=/</c> e sem
    /// <c>Domain</c>, então nem um subdomínio comprometido consegue plantá-lo.
    /// </summary>
    public const string CookieName = "__Host-duora";
}

/// <summary>
/// Ambientes que rodam fora do container de produção e podem dispensar volume para o key ring. Allowlist: um nome
/// novo (Staging, homologação) cai na regra de produção.
/// </summary>
public static class DuoraEnvironments
{
    public const string Testing = "Testing";

    /// <summary>Geração do contrato OpenAPI no build (Duora.Web, BuildTimeOpenApi).</summary>
    public const string BuildTimeOpenApi = "BuildTimeOpenApi";

    public static bool RunsOutsideTheContainer(IHostEnvironment environment) =>
        environment.IsDevelopment()
        || environment.IsEnvironment(Testing)
        || environment.IsEnvironment(BuildTimeOpenApi);
}

internal sealed class DataProtectionStorageOptions
{
    public const string Section = "DataProtection";

    /// <summary>Diretório do key ring, fora da camada gravável do container (spec 11.2).</summary>
    public string KeysDirectory { get; set; } = "";

    /// <summary>
    /// Exige que <see cref="KeysDirectory"/> seja o ponto de montagem de um volume (T-15). Ligado por padrão; só o
    /// desenvolvimento local, os testes e a geração do OpenAPI no build desligam, porque rodam fora do container.
    /// </summary>
    public bool RequireMountedVolume { get; set; } = true;

    public bool IsPersistent() => !RequireMountedVolume || KeyRingMount.IsPersistentMountPoint(KeysDirectory);

    /// <summary>
    /// PEM com o certificado e a chave privada que cifram o key ring em repouso (spec 11.2). Em produção vem do Docker
    /// secret <c>DataProtection__Certificate</c>, gerado pelo <c>duora-secrets</c>.
    /// </summary>
    public string Certificate { get; set; } = "";

    /// <summary>
    /// Certificados anteriores, em pares PEM, só para decifrar as chaves que eles cifraram enquanto a troca não
    /// termina. Vazio fora de uma rotação.
    /// </summary>
    public string PreviousCertificates { get; set; } = "";

    /// <summary>
    /// Exige <see cref="Certificate"/>. Ligado por padrão; só o desenvolvimento local, os testes e a geração do
    /// OpenAPI no build desligam, como em <see cref="RequireMountedVolume"/>.
    /// </summary>
    public bool RequireCertificate { get; set; } = true;
}

public static class WebIdentityServiceCollectionExtensions
{
    /// <summary>
    /// Identidade do processo web: as contas da Infrastructure, mais SignInManager, token providers do Data
    /// Protection, cookie de sessão com ticket no PostgreSQL e Data Protection com key ring persistido. O host não
    /// sobe sem <c>DataProtection:KeysDirectory</c>.
    /// </summary>
    public static IServiceCollection AddDuoraWebIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDuoraDataProtection(configuration);

        services.AddDuoraAccounts()
            .AddUserManager<DuoraUserManager>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddTokenProvider<PasswordResetTokenProvider>(AccountTokenProviders.PasswordReset);
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(24));
        services.Configure<PasswordResetTokenProviderOptions>(o =>
        {
            o.Name = AccountTokenProviders.PasswordReset;
            o.TokenLifespan = TimeSpan.FromMinutes(30);
        });

        services.AddAuthentication(DuoraAuthentication.Scheme)
            .AddCookie(DuoraAuthentication.Scheme, ConfigureCookie)
            .AddCookie(DuoraAuthentication.SignInScheme, ConfigureCookie);
        foreach (var scheme in new[] { DuoraAuthentication.Scheme, DuoraAuthentication.SignInScheme })
        {
            services.AddOptions<CookieAuthenticationOptions>(scheme)
                .Configure<ITicketStore, TimeProvider, IDataProtectionProvider>((options, store, time, dataProtection) =>
                {
                    options.SessionStore = store;
                    options.TimeProvider = time;
                    // Propósito próprio e igual nos dois esquemas: o padrão inclui o nome do esquema, e o cookie
                    // gravado no login não seria lido pelas requisições seguintes.
                    options.TicketDataFormat = new TicketDataFormat(
                        dataProtection.CreateProtector("Duora.Identity.SessionCookie.v1"));
                });
        }

        services.AddSingleton<ITicketStore, PostgresTicketStore>();
        services.AddOptions<LoginTimingOptions>()
            .BindConfiguration(LoginTimingOptions.Section)
            .Validate(o => o.IsValid(), "Identity: os tempos do login com falha não podem ser negativos.")
            .ValidateOnStart();
        services.AddScoped(provider => new CredentialVerifier(
            provider.GetRequiredService<UserManager<DuoraUser>>(),
            provider.GetRequiredService<SignInManager<DuoraUser>>(),
            provider.GetRequiredService<DecoyPasswordHash>(),
            provider.GetRequiredService<IOptions<LoginTimingOptions>>()));

        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = DuoraAuthentication.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.Cookie.Domain = null;

        // O deslize da inatividade é feito no ticket store; o cookie vale até o limite absoluto e é de sessão
        // do navegador (sem "lembrar para sempre", spec 4.3).
        options.SlidingExpiration = false;
        options.ExpireTimeSpan = SessionLifetime.Participant.Absolute;

        // API não redireciona para página de login: 401/403 e o corpo vem do Problem Details.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }

    private static void AddDuoraDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DataProtectionStorageOptions>()
            .BindConfiguration(DataProtectionStorageOptions.Section)
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.KeysDirectory),
                "DataProtection:KeysDirectory é obrigatório: o key ring precisa sobreviver à recriação do processo.")
            .Validate<IHostEnvironment>(
                (o, environment) => o.RequireMountedVolume || DuoraEnvironments.RunsOutsideTheContainer(environment),
                "DataProtection:RequireMountedVolume=false só vale em Development, Testing e BuildTimeOpenApi: fora "
                + "deles o key ring precisa de volume.")
            .Validate(
                o => o.IsPersistent(),
                "DataProtection:KeysDirectory precisa ser o ponto de montagem de um volume persistente: na camada do "
                + "container ou em memória, a chave some no próximo deploy e todas as sessões caem.")
            .Validate<IHostEnvironment>(
                (o, environment) => o.RequireCertificate || DuoraEnvironments.RunsOutsideTheContainer(environment),
                "DataProtection:RequireCertificate=false só vale em Development, Testing e BuildTimeOpenApi: fora "
                + "deles o key ring ficaria em claro no volume e nos backups.")
            .Validate(
                o => !o.RequireCertificate || !string.IsNullOrWhiteSpace(o.Certificate),
                "DataProtection:Certificate é obrigatório: sem ele o key ring fica em claro no volume e nos backups.")
            .Validate(
                o => KeyRingCertificates.IsValid(o.Certificate, maximum: 1),
                "DataProtection:Certificate precisa ser um PEM com um certificado seguido da sua chave privada.")
            .Validate(
                o => KeyRingCertificates.IsValid(o.PreviousCertificates),
                "DataProtection:PreviousCertificates precisa ser vazio ou PEM com pares de certificado e chave privada.")
            .ValidateOnStart();


        // Nome estável: o mesmo key ring precisa ler o que outra instância ou versão protegeu. A vida da chave é a
        // padrão, explícita aqui porque a política depende dela: a cada 90 dias nasce uma chave nova, e as antigas
        // continuam no key ring para ler o que protegeram (sessão de até 12 h, links de até 24 h).
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("Duora")
            .SetDefaultKeyLifetime(TimeSpan.FromDays(90));
        services.AddOptions<KeyManagementOptions>()
            .Configure<IOptions<DataProtectionStorageOptions>, ILoggerFactory>((keys, storage, loggers) =>
                keys.XmlRepository = new FileSystemXmlRepository(
                    new DirectoryInfo(storage.Value.KeysDirectory),
                    loggers))
            // O registro do certificado (abaixo) lê a configuração do momento do registro; as options leem a final.
            // Validar aqui, sobre o que o Data Protection recebeu, pega a divergência em vez de gravar em claro.
            .Validate<IOptions<DataProtectionStorageOptions>>(
                (keys, storage) => !storage.Value.RequireCertificate || keys.XmlEncryptor is CertificateXmlEncryptor,
                "DataProtection:Certificate não chegou ao Data Protection: ele é registrado com a configuração do "
                + "momento do registro, e uma fonte acrescentada depois deixaria o key ring em claro.")
            .ValidateOnStart();

        // Spec 11.2: o atual cifra as chaves novas; o atual e os anteriores decifram as chaves que cada um cifrou. O
        // framework só aceita os certificados no registro, então eles são lidos aqui; se o PEM for inválido, nada
        // é registrado e a validação acima barra o start com a mensagem certa.
        var storage = configuration.GetSection(DataProtectionStorageOptions.Section).Get<DataProtectionStorageOptions>();
        if (KeyRingCertificates.TryLoad(storage?.Certificate, storage?.PreviousCertificates) is { Current: { } current } certificates)
        {
            services.AddSingleton(certificates);
            dataProtection.ProtectKeysWithCertificate(current);
            dataProtection.UnprotectKeysWithAnyCertificate([.. certificates.All]);
        }
    }
}

internal sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions;

/// <summary>Provider de token de redefinição com validade própria, mais curta que a da confirmação de e-mail.</summary>
internal sealed class PasswordResetTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<PasswordResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<DuoraUser>> logger)
    : DataProtectorTokenProvider<DuoraUser>(dataProtectionProvider, options, logger);
