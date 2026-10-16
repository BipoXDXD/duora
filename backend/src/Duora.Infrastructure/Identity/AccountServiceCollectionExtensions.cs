using Duora.Core.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.Infrastructure.Identity;

/// <summary>Nomes dos token providers que a Infrastructure pede e que o host registra.</summary>
public static class AccountTokenProviders
{
    /// <summary>Provider de redefinição de senha, com validade própria (registrado pelo Duora.Web.Identity).</summary>
    public const string PasswordReset = "PasswordReset";
}

public static class AccountServiceCollectionExtensions
{
    /// <summary>
    /// Contas sobre o ASP.NET Core Identity, só com o que não depende do runtime web: usuário, stores no
    /// <c>DuoraDbContext</c>, regras de senha e bloqueio, sessões persistidas e os casos de uso de conta. Devolve o
    /// <see cref="IdentityBuilder"/> para o host acrescentar o que é do runtime web (SignInManager e os token
    /// providers do Data Protection, que o <see cref="AccountService"/> usa ao emitir links).
    /// </summary>
    public static IdentityBuilder AddDuoraAccounts(this IServiceCollection services)
    {
        var identity = services.AddIdentityCore<DuoraUser>(ConfigureIdentity)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<DuoraDbContext>()
            .AddUserStore<DuoraUserStore>();

        services.AddSingleton(provider => new LoginSessions(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddOptions<AccountLifecycleOptions>()
            .BindConfiguration(AccountLifecycleOptions.Section)
            .Validate(o => o.IsValid(), "Identity: PendingAccountLifetime e MaxConfirmationFailures precisam ser positivos.")
            .ValidateOnStart();
        services.AddSingleton<DecoyPasswordHash>();
        services.AddScoped(provider => new AccountService(
            provider.GetRequiredService<DuoraDbContext>(),
            provider.GetRequiredService<UserManager<DuoraUser>>(),
            provider.GetRequiredService<LoginSessions>(),
            provider.GetRequiredService<DecoyPasswordHash>(),
            provider.GetRequiredService<IOptions<AccountLifecycleOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));
        services.AddOptions<AccountLockOptions>()
            .BindConfiguration(AccountLockOptions.Section)
            .Validate(o => o.IsValid(), "Identity:AccountLockTimeout precisa ficar entre 100 ms e 1 min.")
            .ValidateOnStart();
        services.AddSingleton<AccountCheckGate>();
        services.AddScoped(provider => new AccountSecurityService(
            provider.GetRequiredService<DuoraDbContext>(),
            provider.GetRequiredService<UserManager<DuoraUser>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<AccountLockOptions>>().Value,
            provider.GetRequiredService<AccountCheckGate>()));
        services.AddScoped(provider => new AdministratorBootstrap(
            provider.GetRequiredService<DuoraDbContext>(),
            provider.GetRequiredService<UserManager<DuoraUser>>(),
            provider.GetRequiredService<TimeProvider>()));

        return identity;
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

        options.Tokens.PasswordResetTokenProvider = AccountTokenProviders.PasswordReset;
    }
}
