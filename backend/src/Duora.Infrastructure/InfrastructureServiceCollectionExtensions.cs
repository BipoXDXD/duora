using Duora.Core;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Audit;
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
    /// <c>ConnectionStrings:Duora</c> nem sem uma <c>Duora:PublicOrigin</c> https válida (falha no start), nem com um
    /// pool fora da fatia do processo no orçamento de conexões.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        ConnectionShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        services.AddOptions<DatabaseOptions>()
            .Configure(o => o.MaxPoolSize = share.MaxPoolSize)
            .Bind(configuration.GetSection(DatabaseOptions.Section))
            .Configure(o => o.ConnectionString =
                configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) ?? "")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} é obrigatória. "
                + "Em desenvolvimento, defina-a com dotnet user-secrets (docs/development/environment.md).")
            .Validate(
                o => o.FitsIn(share),
                $"Database:MaxPoolSize do {share.Process} precisa ficar entre 1 e {share.MaxPoolSize} (orçamento de "
                + "conexões, deploy.md) e Database:PoolTimeout entre 1 e 30 s, em segundos inteiros.")
            .ValidateOnStart();

        // Um pool só para o EF e para o SQL direto (fila da outbox, sessões), dentro da fatia do processo no
        // orçamento de conexões (spec 10.7).
        services.AddSingleton(provider =>
        {
            var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                MaxPoolSize = database.MaxPoolSize,
                Timeout = (int)database.PoolTimeout.TotalSeconds,
            };
            return NpgsqlDataSource.Create(connection.ConnectionString);
        });
        services.AddSingleton<Duora.Infrastructure.Operations.RetentionService>();
        services.AddDbContext<DuoraDbContext>((provider, options) =>
            options.UseDuoraNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

        services.AddScoped(provider => new AuditTrail(
            provider.GetRequiredService<DuoraDbContext>(),
            provider.GetRequiredService<TimeProvider>()));

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
                $"{PublicOriginOptions.Key} é obrigatória: origem https da SPA, sem caminho (ex.: https://meetduora.com).")
            .ValidateOnStart();

        services.AddSingleton(provider => PublicOriginOptions.Parse(provider.GetRequiredService<IOptions<PublicOriginOptions>>()));
        services.AddOptions<SupportContactOptions>()
            .Configure(o => o.Value = configuration[SupportContactOptions.Key] ?? "")
            .Validate(
                o => EmailAddress.TryParse(o.Value, out _),
                $"{SupportContactOptions.Key} é obrigatório: endereço de e-mail do suporte, citado nos avisos de segurança.")
            .ValidateOnStart();
        services.AddSingleton(provider => new AccountEmails(
            provider.GetRequiredService<PublicOrigin>(),
            SupportContactOptions.Parse(provider.GetRequiredService<IOptions<SupportContactOptions>>())));
    }
}

/// <summary>
/// Contato de suporte dos avisos de segurança. A spec ainda não define o canal: o valor das configurações base é um
/// placeholder (lacuna registrada no auth.md), a trocar antes de abrir a participantes reais.
/// </summary>
internal sealed class SupportContactOptions
{
    public const string Key = "Duora:SupportContact";

    public string Value { get; set; } = "";

    public static EmailAddress Parse(IOptions<SupportContactOptions> options) =>
        EmailAddress.TryParse(options.Value.Value, out var address)
            ? address
            // Inalcançável depois do ValidateOnStart; falha alto se alguém resolver o serviço antes do start.
            : throw new OptionsValidationException(Key, typeof(SupportContactOptions), [$"{Key} inválido."]);
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
