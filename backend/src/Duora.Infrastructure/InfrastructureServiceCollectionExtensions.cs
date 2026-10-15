using Duora.Core;
using Duora.Core.Notifications;
using Duora.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registra banco, origem pública e adaptadores comuns a web e worker. O host não sobe sem
    /// <c>ConnectionStrings:Duora</c> nem sem uma <c>Duora:PublicOrigin</c> https válida (falha no start).
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

        // Um pool só para o EF e para o SQL direto (fila da outbox, sessões), para a conta de conexões da spec 10.7.
        services.AddSingleton(provider =>
            NpgsqlDataSource.Create(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
        services.AddDbContext<DuoraDbContext>((provider, options) =>
            options.UseDuoraNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

        services.AddSingleton(provider =>
            new DatabaseProbe(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        services.AddPublicOrigin(configuration);

        return services;
    }

    private static void AddPublicOrigin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PublicOriginOptions>()
            .Configure(o => o.Value = configuration[PublicOriginOptions.Key] ?? "")
            .Validate(
                o => PublicOrigin.TryParse(o.Value, out _),
                $"{PublicOriginOptions.Key} é obrigatória: origem https da SPA, sem caminho (ex.: https://duora.app).")
            .ValidateOnStart();

        services.AddSingleton(provider => PublicOriginOptions.Parse(provider.GetRequiredService<IOptions<PublicOriginOptions>>()));
        services.AddSingleton(provider => new AccountEmails(provider.GetRequiredService<PublicOrigin>()));
    }
}

internal sealed class PublicOriginOptions
{
    public const string Key = "Duora:PublicOrigin";

    public string Value { get; set; } = "";

    public static PublicOrigin Parse(IOptions<PublicOriginOptions> options) =>
        PublicOrigin.TryParse(options.Value.Value, out var origin)
            ? origin
            // Inalcançável depois do ValidateOnStart; falha alto se alguém resolver o serviço antes do start.
            : throw new OptionsValidationException(Key, typeof(PublicOriginOptions), [$"{Key} inválida."]);
}
