using Duora.Core.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.Infrastructure.Identity;

/// <summary>Nomes do esquema de autenticação por cookie (ADR-0005).</summary>
public static class DuoraAuthentication
{
    public const string Scheme = "Duora.Session";

    /// <summary>
    /// Esquema usado só para gravar a sessão nova no login. O handler do <see cref="Scheme"/> já leu o cookie da
    /// requisição e, no SignIn, reaproveitaria a chave da sessão anterior (chama RenewAsync em vez de StoreAsync),
    /// o que seria session fixation. Este handler nunca autentica, então sempre cria uma chave nova. Os dois
    /// esquemas usam o mesmo cookie e o mesmo formato de ticket.
    /// </summary>
    public const string SignInScheme = "Duora.Session.SignIn";

    /// <summary>
    /// Prefixo <c>__Host-</c>: o navegador só aceita o cookie com <c>Secure</c>, <c>Path=/</c> e sem
    /// <c>Domain</c>, então nem um subdomínio comprometido consegue plantá-lo.
    /// </summary>
    public const string CookieName = "__Host-duora";
}

internal sealed class DataProtectionStorageOptions
{
    public const string Section = "DataProtection";

    /// <summary>Diretório do key ring, fora da camada gravável do container (spec 11.2).</summary>
    public string KeysDirectory { get; set; } = "";
}

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// ASP.NET Core Identity, cookie de sessão com ticket no PostgreSQL e Data Protection com key ring persistido.
    /// O host não sobe sem <c>DataProtection:KeysDirectory</c>.
    /// </summary>
    public static IServiceCollection AddDuoraIdentity(this IServiceCollection services)
    {
        services.AddDuoraDataProtection();

        services.AddIdentityCore<DuoraUser>(ConfigureIdentity)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<DuoraDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddTokenProvider<PasswordResetTokenProvider>(PasswordResetTokenProvider.ProviderName);
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(24));
        services.Configure<PasswordResetTokenProviderOptions>(o =>
        {
            o.Name = PasswordResetTokenProvider.ProviderName;
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

        services.AddSingleton(provider => new LoginSessions(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<TimeProvider>(),
            SessionLifetime.Participant));
        services.AddSingleton<ITicketStore, PostgresTicketStore>();
        services.AddSingleton<CredentialTiming>();
        services.AddScoped(provider => new AccountService(
            provider.GetRequiredService<DuoraDbContext>(),
            provider.GetRequiredService<UserManager<DuoraUser>>(),
            provider.GetRequiredService<SignInManager<DuoraUser>>(),
            provider.GetRequiredService<LoginSessions>(),
            provider.GetRequiredService<CredentialTiming>(),
            provider.GetRequiredService<TimeProvider>()));

        return services;
    }

    private static void ConfigureIdentity(IdentityOptions options)
    {
        // NIST SP 800-63B: só comprimento (o teto de 128 é aplicado na fronteira, pela mesma PasswordPolicy).
        options.Password.RequiredLength = PasswordPolicy.MinLength;
        options.Password.RequiredUniqueChars = 1;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;

        // O nome de usuário é o e-mail, já validado na fronteira; a lista padrão de caracteres recusaria e-mails válidos.
        options.User.AllowedUserNameCharacters = "";
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = true;

        // Bloqueio temporário e silencioso (a resposta é a mesma de senha errada). O limite alto e a janela curta
        // evitam que um atacante tranque a conta de alguém com poucas tentativas; o freio principal contra força
        // bruta é o rate limit por conta e por IP (ADR-0019).
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 20;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

        options.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider.ProviderName;
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

    private static void AddDuoraDataProtection(this IServiceCollection services)
    {
        services.AddOptions<DataProtectionStorageOptions>()
            .BindConfiguration(DataProtectionStorageOptions.Section)
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.KeysDirectory),
                "DataProtection:KeysDirectory é obrigatório: o key ring precisa sobreviver à recriação do processo.")
            .ValidateOnStart();

        // Nome estável: o mesmo key ring precisa ler o que outra instância ou versão protegeu.
        services.AddDataProtection().SetApplicationName("Duora");
        services.AddOptions<KeyManagementOptions>()
            .Configure<IOptions<DataProtectionStorageOptions>, ILoggerFactory>((keys, storage, loggers) =>
                keys.XmlRepository = new FileSystemXmlRepository(
                    new DirectoryInfo(storage.Value.KeysDirectory),
                    loggers));
    }
}

internal sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions;

/// <summary>Provider de token de redefinição com validade própria, mais curta que a da confirmação de e-mail.</summary>
internal sealed class PasswordResetTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<PasswordResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<DuoraUser>> logger)
    : DataProtectorTokenProvider<DuoraUser>(dataProtectionProvider, options, logger)
{
    public const string ProviderName = "PasswordReset";
}
