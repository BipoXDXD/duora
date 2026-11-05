using Duora.Core.Safety;

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
