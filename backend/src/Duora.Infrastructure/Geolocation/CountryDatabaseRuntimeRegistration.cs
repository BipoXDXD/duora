using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.Infrastructure.Geolocation;

public static class CountryDatabaseRuntimeRegistration
{
    public static CountryDatabaseRuntimeOptions AddCountryDatabaseRuntime(this IServiceCollection services,
        IConfiguration configuration, bool worker)
    {
        var section = configuration.GetSection(CountryDatabaseRuntimeOptions.Section);
        var flags = section.Get<CountryDatabaseRuntimeOptions>() ?? new();
        services.AddOptions<CountryDatabaseRuntimeOptions>().Bind(section)
            .Validate(o => o.IsConsistent(worker), "CountryDatabase: configuração operacional incompleta ou incoerente.")
            .Validate<IOptions<DatabaseOptions>>((o, db) => !worker || !o.RetentionEnabled || db.Value.MaxPoolSize <= 8,
                "Database:MaxPoolSize deve ser no máximo 8 com CountryDatabase:RetentionEnabled (2 conexões para manutenção).")
            .ValidateOnStart();
        if (!(worker ? flags.UpdaterEnabled || flags.MonitorEnabled || flags.RetentionEnabled : flags.DetectorEnabled))
        { return flags; }
        services.AddSingleton(p =>
        {
            var o = p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value;
            return new CountryDatabaseArtifactValidator(new(o.DatabaseType, o.MaxBytes, o.MaxNetworks), p.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton(p => new CountryDatabaseUpdateStore(p.GetRequiredService<NpgsqlDataSource>(),
            p.GetRequiredService<TimeProvider>(), p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value.LeaseDuration));
        if (!worker)
        {
            services.AddSingleton(p => new ActiveCountryDatabase(p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value.Root,
                p.GetRequiredService<CountryDatabaseArtifactValidator>(), p.GetRequiredService<TimeProvider>()));
            services.AddSingleton<ICountryLookup>(p => new MmdbCountryLookup(p.GetRequiredService<ActiveCountryDatabase>(), enabled: true));
            return flags;
        }
        if (flags.UpdaterEnabled)
        {
            services.AddSingleton(p =>
            {
                var o = p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value;
                return CountryDatabaseDownloader.Create(new(o.MaxBytes, o.DownloadTimeout, o.RedirectHosts.ToHashSet(StringComparer.Ordinal)),
                    o.IpinfoToken, p.GetRequiredService<TimeProvider>());
            });
            services.AddSingleton(p => new CountryDatabaseUpdateService(p.GetRequiredService<CountryDatabaseUpdateStore>(),
                p.GetRequiredService<CountryDatabaseDownloader>(), p.GetRequiredService<CountryDatabaseArtifactValidator>(),
                p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value.Root,
                p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value.ApprovedInitialHash));
        }
        if (flags.MonitorEnabled || flags.RetentionEnabled)
        { services.AddSingleton(p => new CountryDatabaseOperationsStore(p.GetRequiredService<NpgsqlDataSource>(), p.GetRequiredService<TimeProvider>())); }
        if (flags.MonitorEnabled)
        {
            services.AddSingleton<ICountryDatabaseMailTransport>(p => new GmailCountryDatabaseMailTransport(
                p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value.Gmail));
            services.AddSingleton<ICountryDatabaseNotifier, DurableCountryDatabaseNotifier>();
            services.AddSingleton(p => new CountryDatabaseHealthService(p.GetRequiredService<CountryDatabaseOperationsStore>(),
                p.GetRequiredService<CountryDatabaseArtifactValidator>(), p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value.Root,
                p.GetRequiredService<ICountryDatabaseNotifier>(), enabled: true));
        }
        if (flags.RetentionEnabled)
        {
            services.AddSingleton(p =>
            {
                var o = p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value;
                var c = new NpgsqlConnectionStringBuilder(o.MaintenanceConnectionString) { MaxPoolSize = 2, Timeout = 5 };
                return new CountryDatabaseMaintenanceSource(NpgsqlDataSource.Create(c.ConnectionString));
            });
            services.AddSingleton<ICountryDatabaseArchive>(p => new S3CountryDatabaseArchive(p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value.Archive,
                p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value.MaxBytes));
            services.AddSingleton(p => new CountryDatabaseRetentionService(
                new(p.GetRequiredService<CountryDatabaseMaintenanceSource>().Source, p.GetRequiredService<TimeProvider>()),
                p.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value.Root, p.GetRequiredService<ICountryDatabaseArchive>(), enabled: true));
        }
        return flags;
    }
}

internal sealed class CountryDatabaseMaintenanceSource(NpgsqlDataSource source) : IDisposable
{
    internal NpgsqlDataSource Source { get; } = source;
    public void Dispose() => Source.Dispose();
}
