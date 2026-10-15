namespace Duora.Core.Safety;

/// <summary>
/// Invariantes de programação: só são violadas por um caso de uso com defeito, nunca por entrada da pessoa. Falha
/// esperada usa <see cref="SafetyRejection"/>.
/// </summary>
public enum SafetyRule
{
    /// <summary>O repositório só entrega bloqueios do próprio autor; outro pedido é defeito.</summary>
    OnlyBlockerCanLift,

    /// <summary>Sanção permanente só com autor humano; o sistema apenas prepara a proposta (spec 12.3).</summary>
    PermanentSanctionRequiresHumanAuthor,
    DecisionRequiresEvidence,
    ClearedIsOnlyAnAppealOutcome,

    /// <summary>Defesa em profundidade: a fronteira já filtra por titular e responde 404 (TM-28).</summary>
    AppealOnlyOwnSanction,
}
