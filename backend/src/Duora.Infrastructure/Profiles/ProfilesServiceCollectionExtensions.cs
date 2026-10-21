using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.Infrastructure.Profiles;

public static class ProfilesServiceCollectionExtensions
{
    /// <summary>Perfil do titular (API-P01 e API-P02) sobre o pool compartilhado.</summary>
    public static IServiceCollection AddDuoraProfiles(this IServiceCollection services) =>
        services.AddSingleton<RegionalPreferenceStore>().AddSingleton<PreferenceStore>().AddSingleton<Duora.Infrastructure.Identity.LocaleStore>().AddSingleton<Duora.Infrastructure.Identity.TimeZoneStore>().AddSingleton(provider => new ProfileStore(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<TimeProvider>()));
}
