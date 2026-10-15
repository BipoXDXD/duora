using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.Infrastructure.Safety;

public static class SafetyServiceCollectionExtensions
{
    /// <summary>Casos de uso do Safety. Exige as contas registradas (<c>AddDuoraAccounts</c>), por causa do estado da conta.</summary>
    public static IServiceCollection AddDuoraSafety(this IServiceCollection services)
    {
        services.AddScoped(provider => new BlockService(
            provider.GetRequiredService<DuoraDbContext>(),
            provider.GetRequiredService<AccountService>(),
            provider.GetRequiredService<AuditTrail>(),
            provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
