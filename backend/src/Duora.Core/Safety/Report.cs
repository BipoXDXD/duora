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

    public CaseParties Parties => new(TargetUserId, ReporterId);

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

    public static SafetyResult<Report> Submit(ReportSubmission submission, TimeProvider clock)
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
            return SafetyResult.Rejected<Report>(SafetyRejection.SelfReport);
        }

        var now = clock.GetUtcNow();
        return SafetyResult.Success(new Report(ReportId.New(now), submission, now));
    }

    public static SafetyResult<ReportAndBlock> SubmitAndBlock(ReportSubmission submission, TimeProvider clock)
    {
        var submitted = Submit(submission, clock);
        if (submitted.Rejection is { } rejection)
        {
            return SafetyResult.Rejected<ReportAndBlock>(rejection);
        }

        var report = submitted.Value;
        var block = Block.Create(report.ReporterId, report.TargetUserId, clock).Value;
        return SafetyResult.Success(new ReportAndBlock(report, block));
    }

    /// <summary>Triagem pode ser automatizada, mas automação não rebaixa denúncia grave (spec 12.3, 19.3).</summary>
    public SafetyResult Triage(ReportPriority priority, ModerationAuthor author)
    {
        ArgumentNullException.ThrowIfNull(author);
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Prioridade desconhecida.");
        }

        if (State != ReportState.Received)
        {
            return SafetyResult.Rejected(SafetyRejection.TransitionNotAllowed);
        }

        if (author is ModerationAuthor.Human human && Parties.Includes(human.UserId))
        {
            return SafetyResult.Rejected(SafetyRejection.ReviewerMustBeIndependent);
        }

        if (IsSevere(Category) && priority < ReportPriority.Urgent && author is ModerationAuthor.Automated)
        {
            return SafetyResult.Rejected(SafetyRejection.AutomatedTriageCannotLowerSevereReport);
        }

        Priority = priority;
        State = ReportState.Triaged;
        return SafetyResult.Success();
    }

    public SafetyResult StartReview(Guid moderatorId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(moderatorId, Guid.Empty);
        if (State != ReportState.Triaged)
        {
            return SafetyResult.Rejected(SafetyRejection.TransitionNotAllowed);
        }

        if (Parties.Includes(moderatorId))
        {
            return SafetyResult.Rejected(SafetyRejection.ReviewerMustBeIndependent);
        }

        AssignedModeratorId = moderatorId;
        State = ReportState.UnderReview;
        return SafetyResult.Success();
    }

    /// <summary>A decisão é do moderador atribuído; a automação só prepara a proposta.</summary>
    public SafetyResult<ModerationAction> TakeAction(
        ModerationDecision decision,
        ModerationLimits limits,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (RejectionToDecide(decision.Author) is { } rejection)
        {
            return SafetyResult.Rejected<ModerationAction>(rejection);
        }

        var decided = ModerationAction.Decide(Parties, Id, decision, limits, clock);
        if (decided.IsSuccess)
        {
            CurrentActionId = decided.Value.Id;
            State = ReportState.Actioned;
        }

        return decided;
    }

    public SafetyResult Close(Guid moderatorId, string reason, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (RejectionToDecide(new ModerationAuthor.Human(moderatorId)) is { } rejection)
        {
            return SafetyResult.Rejected(rejection);
        }

        ClosingReason = SafetyText.RequireBounded(reason, ClosingReasonMaxLength, allowEmpty: false, nameof(reason));
        ClosedAt = clock.GetUtcNow();
        State = ReportState.Closed;
        return SafetyResult.Success();
    }

    public SafetyResult MarkAppealed(Appeal appeal)
    {
        ArgumentNullException.ThrowIfNull(appeal);
        if (State != ReportState.Actioned)
        {
            return SafetyResult.Rejected(SafetyRejection.TransitionNotAllowed);
        }

        if (appeal.ModerationActionId != CurrentActionId)
        {
            throw new ArgumentException("O recurso não é sobre a medida desta denúncia.", nameof(appeal));
        }

        State = ReportState.Appealed;
        return SafetyResult.Success();
    }

    /// <summary>
    /// Registra a revisão do recurso. A medida anterior continua existindo; o caso passa a apontar para a revisão.
    /// </summary>
    public SafetyResult ResolveAppeal(ModerationAction revision, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(clock);
        if (State != ReportState.Appealed)
        {
            return SafetyResult.Rejected(SafetyRejection.TransitionNotAllowed);
        }

        if (revision.SupersedesActionId != CurrentActionId)
        {
            throw new ArgumentException("A revisão não substitui a medida desta denúncia.", nameof(revision));
        }

        CurrentActionId = revision.Id;
        if (revision.Measure == ModerationMeasure.Cleared)
        {
            ClosingReason = revision.InternalReason;
            ClosedAt = clock.GetUtcNow();
            State = ReportState.Closed;
            return SafetyResult.Success();
        }

        State = ReportState.Actioned;
        return SafetyResult.Success();
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

    private SafetyRejection? RejectionToDecide(ModerationAuthor author)
    {
        if (State != ReportState.UnderReview)
        {
            return SafetyRejection.TransitionNotAllowed;
        }

        return AssignedModeratorId is { } moderatorId && author.Is(moderatorId)
            ? null
            : SafetyRejection.OnlyAssignedReviewerCanDecide;
    }
}
