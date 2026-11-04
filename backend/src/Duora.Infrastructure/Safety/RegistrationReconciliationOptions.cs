using Duora.Core.Safety;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.Infrastructure.Safety;

/// <summary>
/// Seção <c>RegistrationReconciliation:Cutover</c>: descritor revisado do corte da issue #321, com
/// <c>EvidenceId</c>, <c>Lower</c> e <c>Upper</c> (instantes UTC com sufixo <c>Z</c>). Ausente por padrão: a prévia
/// recebe corte nulo e nada é candidato (<c>MissingDeploymentEvidence</c>). Nenhum corte real é cadastrado no
/// repositório; preenchê-lo exige evidência de implantação revisada (proposta, seção 4).
/// </summary>
public sealed class RegistrationReconciliationCutoverOptions
{
    public const string Section = "RegistrationReconciliation:Cutover";

    public string? EvidenceId { get; set; }

    public string? Lower { get; set; }

    public string? Upper { get; set; }

    /// <summary>Nenhum campo informado: a seção está ausente, o que é válido e significa "sem corte".</summary>
    public bool IsAbsent => EvidenceId is null && Lower is null && Upper is null;

    public bool IsValid() => IsAbsent || ReconciliationCutoverEvidence.TryParse(EvidenceId, Lower, Upper, out _);

    /// <summary>O descritor, ou null sem seção. Depois do <c>ValidateOnStart</c>, nunca é inválido.</summary>
    public ReconciliationCutoverEvidence? Evidence() =>
        IsAbsent ? null
        : ReconciliationCutoverEvidence.TryParse(EvidenceId, Lower, Upper, out var evidence) ? evidence
        : throw new InvalidOperationException($"{Section} inválido depois da validação de inicialização.");
}

public static class RegistrationReconciliationServiceCollectionExtensions
{
    /// <summary>
    /// Leitor da prévia (API-ADM25) e o descritor do corte. Só a Web registra: não há executor, job nem worker da
    /// reconciliação nesta etapa.
    /// </summary>
    public static IServiceCollection AddRegistrationReconciliationPreview(this IServiceCollection services)
    {
        services.AddOptions<RegistrationReconciliationCutoverOptions>()
            .BindConfiguration(RegistrationReconciliationCutoverOptions.Section)
            .Validate(
                o => o.IsValid(),
                $"{RegistrationReconciliationCutoverOptions.Section}: informe EvidenceId (letras, dígitos, '-', '_', '.' "
                + "ou ':', até 128), Lower e Upper juntos, instantes UTC com sufixo Z e Upper >= Lower; ou omita a seção.")
            .ValidateOnStart();
        services.AddSingleton(provider => new LegacyRegistrationReconciliationPreviews(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
