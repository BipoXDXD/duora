using Duora.Infrastructure.Safety;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.Infrastructure.Operations;

public static class RegistrationReconciliationServiceCollectionExtensions
{
    /// <summary>
    /// Prévia (API-ADM25), runs (API-ADM26 e API-ADM27) e o descritor do corte. Só a Web registra: não há executor, job
    /// nem worker da reconciliação nesta etapa.
    /// </summary>
    public static IServiceCollection AddRegistrationReconciliation(this IServiceCollection services)
    {
        services.AddOptions<RegistrationReconciliationCutoverOptions>()
            .BindConfiguration(RegistrationReconciliationCutoverOptions.Section)
            .Validate(
                o => o.IsValid(),
                $"{RegistrationReconciliationCutoverOptions.Section}: informe EvidenceId (letras, dígitos, '-', '_', '.' "
                + "ou ':', até 128), Lower e Upper juntos, instantes UTC com sufixo Z, até seis casas decimais e "
                + "Upper >= Lower; ou omita a seção.")
            .ValidateOnStart();
        services.AddSingleton(provider => new LegacyRegistrationReconciliationPreviews(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<RegistrationReconciliationRuns>();
        return services;
    }
}
