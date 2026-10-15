using Duora.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registra banco e adaptadores. O host não sobe sem <c>ConnectionStrings:Duora</c> (falha no start).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Configure(o => o.ConnectionString =
                configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) ?? "")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} é obrigatória. "
                + "Em desenvolvimento, defina-a com dotnet user-secrets (docs/development/environment.md).")
            .ValidateOnStart();

        services.AddDbContext<DuoraDbContext>((provider, options) =>
            options.UseDuoraNpgsql(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        services.AddSingleton(provider =>
            new DatabaseProbe(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        return services;
    }
}
