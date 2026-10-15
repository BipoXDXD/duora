namespace Duora.Core.Safety;

/// <summary>
/// Medida de moderação (INV-10): guarda versão da política, evidência usada, medida, motivo, autor, instante e se
/// pode ser revista. É imutável: o recurso cria uma revisão nova que aponta para esta, sem apagá-la.
/// </summary>
/// <remarks>
/// Os campos são <c>required init</c> e o construtor é privado: só as fábricas desta classe criam a medida, já
/// validada, sem um construtor posicional de dez parâmetros.
/// </remarks>
public sealed class ModerationAction
{
    /// <summary>Em caracteres Unicode; mesmo teto do `reason` em safety.moderation_actions.</summary>
    private const int ReasonMaxLength = 2000;

    private ModerationAction()
    {
    }

    public required ModerationActionId Id { get; init; }

    /// <summary>Quem sofre a medida e quem denunciou, para que nenhum dos dois revise o recurso.</summary>
    public required CaseParties Parties { get; init; }

    public Guid SubjectUserId => Parties.SubjectUserId;

    /// <summary>Nulo quando a medida nasce de sinal de abuso, sem denúncia.</summary>
    public required ReportId? ReportId { get; init; }

    public required ModerationMeasure Measure { get; init; }

    public required PolicyVersion PolicyVersion { get; init; }

    public required IReadOnlyList<Guid> EvidenceIds { get; init; }

    /// <summary>Motivo para auditoria (INV-10); pode citar investigação e nunca vai para a pessoa.</summary>
    public required string InternalReason { get; init; }

    public required SubjectNotice Notice { get; init; }

    public required ModerationAuthor Author { get; init; }

    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>Fim exclusivo da medida; nulo para advertência, banimento permanente e revisão que reverte.</summary>
    public required DateTimeOffset? EffectiveUntil { get; init; }

    public required ModerationActionId? SupersedesActionId { get; init; }

    /// <summary>Há um nível de recurso: a decisão do recurso não admite outro (proposta; a spec não define).</summary>
    public bool IsReviewable => SupersedesActionId is null;

    public static SafetyResult<ModerationAction> DecideWithoutReport(
        Guid subjectUserId,
        ModerationDecision decision,
        ModerationLimits limits,
        TimeProvider clock) =>
        Decide(new CaseParties(subjectUserId, reporterId: null), reportId: null, decision, limits, clock);

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
                Notice.ReasonCode,
                Notice.Text,
                PolicyVersion,
                DecidedAt,
                EffectiveUntil,
                DecidedAutomatically: Author is ModerationAuthor.Automated,
                CanAppeal: IsReviewable);

    internal static SafetyResult<ModerationAction> Decide(
        CaseParties parties,
        ReportId? reportId,
        ModerationDecision decision,
        ModerationLimits limits,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(parties);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(clock);
        RequireInvariants(decision);
        if (DurationRejection(decision, limits) is { } rejection)
        {
            return SafetyResult.Rejected<ModerationAction>(rejection);
        }

        var decidedAt = clock.GetUtcNow();
        return SafetyResult.Success(new ModerationAction
        {
            Id = ModerationActionId.New(decidedAt),
            Parties = parties,
            ReportId = reportId,
            Measure = decision.Measure,
            PolicyVersion = decision.PolicyVersion,
            EvidenceIds = [.. decision.EvidenceIds],
            InternalReason = RequireInternalReason(decision.InternalReason),
            Notice = decision.Notice,
            Author = decision.Author,
            DecidedAt = decidedAt,
            EffectiveUntil = decidedAt + decision.Duration,
            SupersedesActionId = null,
        });
    }

    internal ModerationAction Revise(
        ModerationMeasure measure,
        DateTimeOffset? effectiveUntil,
        AppealRuling ruling,
        Guid reviewerId,
        DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(ruling);
        ArgumentNullException.ThrowIfNull(ruling.PolicyVersion);
        ArgumentNullException.ThrowIfNull(ruling.Notice);
        return new ModerationAction
        {
            Id = ModerationActionId.New(decidedAt),
            Parties = Parties,
            ReportId = ReportId,
            Measure = measure,
            PolicyVersion = ruling.PolicyVersion,
            EvidenceIds = EvidenceIds,
            InternalReason = RequireInternalReason(ruling.InternalReason),
            Notice = ruling.Notice,
            Author = new ModerationAuthor.Human(reviewerId),
            DecidedAt = decidedAt,
            EffectiveUntil = effectiveUntil,
            SupersedesActionId = Id,
        };
    }

    private static string RequireInternalReason(string reason) =>
        SafetyText.RequireBounded(reason, ReasonMaxLength, allowEmpty: false, nameof(reason));

    private static void RequireInvariants(ModerationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(decision.PolicyVersion);
        ArgumentNullException.ThrowIfNull(decision.Notice);
        ArgumentNullException.ThrowIfNull(decision.EvidenceIds);
        ArgumentNullException.ThrowIfNull(decision.Author);
        if (!Enum.IsDefined(decision.Measure))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision.Measure, "Medida desconhecida.");
        }

        if (decision.Measure == ModerationMeasure.Cleared)
        {
            throw new SafetyRuleViolationException(SafetyRule.ClearedIsOnlyAnAppealOutcome);
        }

        if (decision.Author is ModerationAuthor.Automated && decision.Measure != ModerationMeasure.Restriction)
        {
            throw new SafetyRuleViolationException(SafetyRule.MeasureRequiresHumanAuthor);
        }

        if (decision.EvidenceIds.Count == 0 || decision.EvidenceIds.Contains(Guid.Empty))
        {
            throw new SafetyRuleViolationException(SafetyRule.DecisionRequiresEvidence);
        }
    }

    /// <summary>Compara com o teto antes de somar ao instante, para nenhuma duração causar overflow.</summary>
    private static SafetyRejection? DurationRejection(ModerationDecision decision, ModerationLimits limits)
    {
        var isTemporary = decision.Measure is ModerationMeasure.Restriction or ModerationMeasure.Suspension;
        var shapeFits = isTemporary ? decision.Duration > TimeSpan.Zero : decision.Duration is null;
        if (!shapeFits)
        {
            return SafetyRejection.MeasureDurationMismatch;
        }

        return decision.Duration > limits.MaxDurationFor(decision.Author) ? SafetyRejection.DurationExceedsLimit : null;
    }
}
