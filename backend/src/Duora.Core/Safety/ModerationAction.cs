namespace Duora.Core.Safety;

/// <summary>
/// Medida de moderação (INV-10): guarda versão da política, evidência usada, medida, motivo, autor, instante e se
/// pode ser revista. É imutável: o recurso cria uma revisão nova que aponta para esta, sem apagá-la.
/// </summary>
public sealed class ModerationAction
{
    /// <summary>Em caracteres Unicode; mesmo teto do `reason` em safety.moderation_actions.</summary>
    private const int ReasonMaxLength = 2000;

    private readonly ModerationDecision _terms;

    private ModerationAction(
        ModerationActionId id,
        Guid subjectUserId,
        ReportId? reportId,
        ModerationDecision terms,
        DateTimeOffset decidedAt,
        DateTimeOffset? effectiveUntil,
        ModerationActionId? supersedesActionId)
    {
        Id = id;
        SubjectUserId = subjectUserId;
        ReportId = reportId;
        _terms = terms;
        DecidedAt = decidedAt;
        EffectiveUntil = effectiveUntil;
        SupersedesActionId = supersedesActionId;
    }

    public ModerationActionId Id { get; }

    public Guid SubjectUserId { get; }

    /// <summary>Nulo quando a medida nasce de sinal de abuso, sem denúncia.</summary>
    public ReportId? ReportId { get; }

    public ModerationMeasure Measure => _terms.Measure;

    public PolicyVersion PolicyVersion => _terms.PolicyVersion;

    public IReadOnlyList<Guid> EvidenceIds => _terms.EvidenceIds;

    public string Reason => _terms.Reason;

    public ModerationAuthor Author => _terms.Author;

    public DateTimeOffset DecidedAt { get; }

    /// <summary>Fim exclusivo da medida; nulo para advertência, banimento permanente e revisão que reverte.</summary>
    public DateTimeOffset? EffectiveUntil { get; }

    public ModerationActionId? SupersedesActionId { get; }

    /// <summary>Há um nível de recurso: a decisão do recurso não admite outro (proposta; a spec não define).</summary>
    public bool IsReviewable => SupersedesActionId is null;

    public static ModerationAction DecideWithoutReport(
        Guid subjectUserId,
        ModerationDecision decision,
        TimeProvider clock) =>
        Decide(subjectUserId, reportId: null, decision, clock);

    public bool RestrictsInteractionAt(DateTimeOffset instant) =>
        Measure is ModerationMeasure.Restriction or ModerationMeasure.Suspension or ModerationMeasure.PermanentBan
        && instant >= DecidedAt
        && (EffectiveUntil is null || instant < EffectiveUntil);

    /// <summary>
    /// O que a pessoa sancionada vê. Sem denúncia, denunciante, evidência nem identidade do moderador; diz só se a
    /// decisão foi automatizada, porque isso dá direito a pedir revisão humana.
    /// </summary>
    public SanctionNoticeView? GetSanctionNotice(Guid requesterId) =>
        requesterId != SubjectUserId
            ? null
            : new SanctionNoticeView(
                Id,
                Measure,
                Reason,
                PolicyVersion,
                DecidedAt,
                EffectiveUntil,
                DecidedAutomatically: Author is ModerationAuthor.Automated,
                CanAppeal: IsReviewable);

    internal static ModerationAction Decide(
        Guid subjectUserId,
        ReportId? reportId,
        ModerationDecision decision,
        TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(subjectUserId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(clock);
        var terms = ValidTerms(decision);
        var decidedAt = clock.GetUtcNow();
        return new ModerationAction(
            ModerationActionId.New(decidedAt),
            subjectUserId,
            reportId,
            terms,
            decidedAt,
            decidedAt + decision.Duration,
            supersedesActionId: null);
    }

    internal ModerationAction Revise(
        ModerationMeasure measure,
        DateTimeOffset? effectiveUntil,
        string reason,
        PolicyVersion policyVersion,
        Guid reviewerId,
        DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(policyVersion);
        var terms = new ModerationDecision(
            measure,
            policyVersion,
            EvidenceIds,
            SafetyText.RequireBounded(reason, ReasonMaxLength, allowEmpty: false, nameof(reason)),
            new ModerationAuthor.Human(reviewerId));
        return new ModerationAction(
            ModerationActionId.New(decidedAt),
            SubjectUserId,
            ReportId,
            terms,
            decidedAt,
            effectiveUntil,
            supersedesActionId: Id);
    }

    private static ModerationDecision ValidTerms(ModerationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(decision.PolicyVersion);
        ArgumentNullException.ThrowIfNull(decision.EvidenceIds);
        ArgumentNullException.ThrowIfNull(decision.Author);
        if (!Enum.IsDefined(decision.Measure))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision.Measure, "Medida desconhecida.");
        }

        RequireMeasureRules(decision);
        if (decision.EvidenceIds.Count == 0 || decision.EvidenceIds.Contains(Guid.Empty))
        {
            throw new SafetyRuleViolationException(SafetyRule.DecisionRequiresEvidence);
        }

        return decision with
        {
            EvidenceIds = [.. decision.EvidenceIds],
            Reason = SafetyText.RequireBounded(decision.Reason, ReasonMaxLength, allowEmpty: false, nameof(decision)),
            Duration = null,
        };
    }

    private static void RequireMeasureRules(ModerationDecision decision)
    {
        if (decision.Measure == ModerationMeasure.Cleared)
        {
            throw new SafetyRuleViolationException(SafetyRule.ClearedIsOnlyAnAppealOutcome);
        }

        if (decision.Measure == ModerationMeasure.PermanentBan && decision.Author is not ModerationAuthor.Human)
        {
            throw new SafetyRuleViolationException(SafetyRule.PermanentSanctionRequiresHumanAuthor);
        }

        var isTemporary = decision.Measure is ModerationMeasure.Restriction or ModerationMeasure.Suspension;
        var durationFits = isTemporary ? decision.Duration > TimeSpan.Zero : decision.Duration is null;
        if (!durationFits)
        {
            throw new SafetyRuleViolationException(SafetyRule.MeasureDurationMismatch);
        }
    }
}
