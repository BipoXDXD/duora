namespace Duora.Core.Safety;

/// <summary>
/// Denúncia (aggregate). Estados e transições da spec 4.2:
/// Received → Triaged → UnderReview → Actioned | Closed; Actioned → Appealed → Actioned | Closed.
/// Denunciar não bloqueia nem sanciona ninguém por si só (spec 3.2, 7.3, TM-33).
/// </summary>
public sealed class Report
{
    private const int ClosingReasonMaxLength = 2000;

    private Report(ReportId id, ReportSubmission submission, DateTimeOffset submittedAt)
    {
        Id = id;
        ReporterId = submission.ReporterId;
        TargetUserId = submission.TargetUserId;
        Category = submission.Category;
        Context = submission.Context;
        Narrative = submission.Narrative;
        SubmittedAt = submittedAt;
        State = ReportState.Received;
        Priority = IsSevere(submission.Category) ? ReportPriority.Urgent : ReportPriority.Normal;
    }

    /// <summary>É o protocolo que a pessoa usa para acompanhar a denúncia.</summary>
    public ReportId Id { get; }

    public Guid ReporterId { get; }

    public Guid TargetUserId { get; }

    public ReportCategory Category { get; }

    public ReportContext Context { get; }

    public ReportNarrative Narrative { get; }

    public DateTimeOffset SubmittedAt { get; }

    public ReportState State { get; private set; }

    public ReportPriority Priority { get; private set; }

    public Guid? AssignedModeratorId { get; private set; }

    /// <summary>Medida em vigor do caso; após recurso, a revisão mais recente.</summary>
    public ModerationActionId? CurrentActionId { get; private set; }

    public string? ClosingReason { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public static Report Submit(ReportSubmission submission, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfEqual(submission.ReporterId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(submission.TargetUserId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(submission.Context);
        ArgumentNullException.ThrowIfNull(submission.Narrative);
        if (!Enum.IsDefined(submission.Category))
        {
            throw new ArgumentOutOfRangeException(nameof(submission), submission.Category, "Categoria desconhecida.");
        }

        if (submission.ReporterId == submission.TargetUserId)
        {
            throw new SafetyRuleViolationException(SafetyRule.SelfReport);
        }

        var now = clock.GetUtcNow();
        return new Report(ReportId.New(now), submission, now);
    }

    public static ReportAndBlock SubmitAndBlock(ReportSubmission submission, TimeProvider clock)
    {
        var report = Submit(submission, clock);
        return new ReportAndBlock(report, Block.Create(report.ReporterId, report.TargetUserId, clock));
    }

    /// <summary>Triagem pode ser automatizada, mas automação não rebaixa denúncia grave (spec 12.3, 19.3).</summary>
    public void Triage(ReportPriority priority, ModerationAuthor author)
    {
        ArgumentNullException.ThrowIfNull(author);
        RequireState(ReportState.Received);
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Prioridade desconhecida.");
        }

        if (IsSevere(Category) && priority < ReportPriority.Urgent && author is ModerationAuthor.Automated)
        {
            throw new SafetyRuleViolationException(SafetyRule.AutomatedTriageCannotLowerSevereReport);
        }

        Priority = priority;
        State = ReportState.Triaged;
    }

    public void StartReview(Guid moderatorId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(moderatorId, Guid.Empty);
        RequireState(ReportState.Triaged);
        if (moderatorId == ReporterId || moderatorId == TargetUserId)
        {
            throw new SafetyRuleViolationException(SafetyRule.ReviewerMustBeIndependent);
        }

        AssignedModeratorId = moderatorId;
        State = ReportState.UnderReview;
    }

    /// <summary>A decisão é do moderador atribuído; a automação só prepara a proposta.</summary>
    public ModerationAction TakeAction(ModerationDecision decision, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(decision);
        RequireState(ReportState.UnderReview);
        RequireAssignedModerator(decision.Author);

        var action = ModerationAction.Decide(TargetUserId, Id, decision, clock);
        CurrentActionId = action.Id;
        State = ReportState.Actioned;
        return action;
    }

    public void Close(Guid moderatorId, string reason, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RequireState(ReportState.UnderReview);
        RequireAssignedModerator(new ModerationAuthor.Human(moderatorId));

        ClosingReason = SafetyText.RequireBounded(reason, ClosingReasonMaxLength, allowEmpty: false, nameof(reason));
        ClosedAt = clock.GetUtcNow();
        State = ReportState.Closed;
    }

    public void MarkAppealed(Appeal appeal)
    {
        ArgumentNullException.ThrowIfNull(appeal);
        RequireState(ReportState.Actioned);
        if (appeal.ModerationActionId != CurrentActionId)
        {
            throw new ArgumentException("O recurso não é sobre a medida desta denúncia.", nameof(appeal));
        }

        State = ReportState.Appealed;
    }

    /// <summary>
    /// Registra a revisão do recurso. A medida anterior continua existindo; o caso passa a apontar para a revisão.
    /// </summary>
    public void ResolveAppeal(ModerationAction revision, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(clock);
        RequireState(ReportState.Appealed);
        if (revision.SupersedesActionId != CurrentActionId)
        {
            throw new ArgumentException("A revisão não substitui a medida desta denúncia.", nameof(revision));
        }

        CurrentActionId = revision.Id;
        if (revision.Measure == ModerationMeasure.Cleared)
        {
            ClosingReason = revision.Reason;
            ClosedAt = clock.GetUtcNow();
            State = ReportState.Closed;
            return;
        }

        State = ReportState.Actioned;
    }

    /// <summary>Só quem denunciou vê o protocolo; para qualquer outra pessoa, a denúncia não existe.</summary>
    public ReportStatusView? GetReportStatus(Guid requesterId) =>
        requesterId != ReporterId ? null : new ReportStatusView(Id, Category, StatusForReporter(), SubmittedAt);

    private static bool IsSevere(ReportCategory category) =>
        category is ReportCategory.ThreatOfViolence or ReportCategory.MinorSafety;

    private ReportStatusForReporter StatusForReporter() => State switch
    {
        ReportState.Received or ReportState.Triaged => ReportStatusForReporter.Received,
        ReportState.UnderReview => ReportStatusForReporter.UnderReview,
        ReportState.Actioned or ReportState.Closed or ReportState.Appealed => ReportStatusForReporter.Concluded,
        _ => throw new InvalidOperationException($"Estado de denúncia sem projeção: {State}."),
    };

    private void RequireState(ReportState expected)
    {
        if (State != expected)
        {
            throw new SafetyRuleViolationException(SafetyRule.TransitionNotAllowed);
        }
    }

    private void RequireAssignedModerator(ModerationAuthor author)
    {
        if (AssignedModeratorId is not { } moderatorId || !author.Is(moderatorId))
        {
            throw new SafetyRuleViolationException(SafetyRule.OnlyAssignedReviewerCanDecide);
        }
    }
}
