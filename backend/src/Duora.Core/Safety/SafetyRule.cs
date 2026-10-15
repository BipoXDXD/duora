namespace Duora.Core.Safety;

/// <summary>
/// Invariantes de programação: só são violadas por um caso de uso com defeito, nunca por entrada da pessoa. Falha
/// esperada usa <see cref="SafetyRejection"/>.
/// </summary>
public enum SafetyRule
{
    /// <summary>O repositório só entrega bloqueios do próprio autor; outro pedido é defeito.</summary>
    OnlyBlockerCanLift,

    /// <summary>
    /// O sistema só aplica restrição de contenção; advertência, suspensão e banimento exigem pessoa, e a automação
    /// apenas prepara a proposta (spec 12.3, TM-34).
    /// </summary>
    MeasureRequiresHumanAuthor,
    DecisionRequiresEvidence,
    ClearedIsOnlyAnAppealOutcome,

    /// <summary>Defesa em profundidade: a fronteira já filtra por titular e responde 404 (TM-28).</summary>
    AppealOnlyOwnSanction,
}
