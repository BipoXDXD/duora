namespace Duora.Core.Safety;

/// <summary>Regras de negócio do Safety; a fronteira HTTP traduz cada uma para a resposta adequada.</summary>
public enum SafetyRule
{
    SelfBlock,
    OnlyBlockerCanLift,
    SelfReport,
    TransitionNotAllowed,

    /// <summary>Quem revisa não pode ser parte do caso nem autor da decisão revista (spec 12.3, TM-34).</summary>
    ReviewerMustBeIndependent,
    OnlyAssignedReviewerCanDecide,

    /// <summary>Sanção permanente só com autor humano; o sistema apenas prepara a proposta (spec 12.3).</summary>
    PermanentSanctionRequiresHumanAuthor,

    /// <summary>Classificador não rebaixa denúncia grave; só uma pessoa pode (spec 19.3).</summary>
    AutomatedTriageCannotLowerSevereReport,
    DecisionRequiresEvidence,
    MeasureDurationMismatch,
    ClearedIsOnlyAnAppealOutcome,

    /// <summary>A fronteira responde 404, como para medida inexistente (TM-28).</summary>
    AppealOnlyOwnSanction,
    ActionNotAppealable,
}
