namespace Duora.Core.Safety;

public enum AppealState
{
    Received,
    UnderReview,
    Upheld,
    Overturned,
}

/// <summary>
/// Recurso contra uma medida própria (API-S06). A decisão é uma revisão nova, feita por uma pessoa independente
/// da decisão original; a medida recorrida não muda nem some (spec 4.2, 12.3).
/// </summary>
public sealed class Appeal
{
    /// <summary>Em caracteres Unicode; mesmo teto do `reason` em safety.appeals.</summary>
    public const int ReasonMaxLength = 2000;

    private readonly ModerationAction _original;

    private Appeal(AppealId id, ModerationAction original, string reason, DateTimeOffset filedAt)
    {
        Id = id;
        _original = original;
        Reason = reason;
        FiledAt = filedAt;
        State = AppealState.Received;
    }

    public AppealId Id { get; }

    public ModerationActionId ModerationActionId => _original.Id;

    public Guid AppellantId => _original.SubjectUserId;

    public string Reason { get; }

    public DateTimeOffset FiledAt { get; }

    public AppealState State { get; private set; }

    public Guid? ReviewerId { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public static Appeal File(ModerationAction action, Guid appellantId, string reason, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(clock);
        if (appellantId != action.SubjectUserId)
        {
            throw new SafetyRuleViolationException(SafetyRule.AppealOnlyOwnSanction);
        }

        if (!action.IsReviewable)
        {
            throw new SafetyRuleViolationException(SafetyRule.ActionNotAppealable);
        }

        var validReason = SafetyText.RequireBounded(reason, ReasonMaxLength, allowEmpty: false, nameof(reason));
        var now = clock.GetUtcNow();
        return new Appeal(AppealId.New(now), action, validReason, now);
    }

    /// <summary>Recurso é revisão humana; não repete o autor nem o classificador da decisão original.</summary>
    public void StartReview(Guid reviewerId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(reviewerId, Guid.Empty);
        RequireState(AppealState.Received);
        if (_original.Author.Is(reviewerId) || reviewerId == AppellantId)
        {
            throw new SafetyRuleViolationException(SafetyRule.ReviewerMustBeIndependent);
        }

        ReviewerId = reviewerId;
        State = AppealState.UnderReview;
    }

    /// <summary>Mantém a medida: a revisão repete medida e fim, com o motivo e a política do revisor.</summary>
    public ModerationAction Uphold(Guid reviewerId, string reason, PolicyVersion policyVersion, TimeProvider clock) =>
        Decide(reviewerId, AppealState.Upheld, _original.Measure, _original.EffectiveUntil, reason, policyVersion, clock);

    public ModerationAction Overturn(Guid reviewerId, string reason, PolicyVersion policyVersion, TimeProvider clock) =>
        Decide(reviewerId, AppealState.Overturned, ModerationMeasure.Cleared, null, reason, policyVersion, clock);

    private ModerationAction Decide(
        Guid reviewerId,
        AppealState outcome,
        ModerationMeasure measure,
        DateTimeOffset? effectiveUntil,
        string reason,
        PolicyVersion policyVersion,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RequireState(AppealState.UnderReview);
        if (reviewerId != ReviewerId)
        {
            throw new SafetyRuleViolationException(SafetyRule.OnlyAssignedReviewerCanDecide);
        }

        var now = clock.GetUtcNow();
        var revision = _original.Revise(measure, effectiveUntil, reason, policyVersion, reviewerId, now);
        DecidedAt = now;
        State = outcome;
        return revision;
    }

    private void RequireState(AppealState expected)
    {
        if (State != expected)
        {
            throw new SafetyRuleViolationException(SafetyRule.TransitionNotAllowed);
        }
    }
}
