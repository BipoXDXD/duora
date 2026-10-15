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

    public static SafetyResult<Appeal> File(ModerationAction action, Guid appellantId, string reason, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(clock);
        if (appellantId != action.SubjectUserId)
        {
            throw new SafetyRuleViolationException(SafetyRule.AppealOnlyOwnSanction);
        }

        var validReason = SafetyText.RequireBounded(reason, ReasonMaxLength, allowEmpty: false, nameof(reason));
        if (!action.IsReviewable)
        {
            return SafetyResult.Rejected<Appeal>(SafetyRejection.ActionNotAppealable);
        }

        var now = clock.GetUtcNow();
        return SafetyResult.Success(new Appeal(AppealId.New(now), action, validReason, now));
    }

    /// <summary>Recurso é revisão humana; não repete o autor nem o classificador da decisão original.</summary>
    public SafetyResult StartReview(Guid reviewerId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(reviewerId, Guid.Empty);
        if (State != AppealState.Received)
        {
            return SafetyResult.Rejected(SafetyRejection.TransitionNotAllowed);
        }

        if (_original.Author.Is(reviewerId) || _original.Parties.Includes(reviewerId))
        {
            return SafetyResult.Rejected(SafetyRejection.ReviewerMustBeIndependent);
        }

        ReviewerId = reviewerId;
        State = AppealState.UnderReview;
        return SafetyResult.Success();
    }

    /// <summary>Mantém a medida: a revisão repete medida e fim, com o motivo e a política do revisor.</summary>
    public SafetyResult<ModerationAction> Uphold(Guid reviewerId, AppealRuling ruling, TimeProvider clock) =>
        Decide(reviewerId, AppealState.Upheld, _original.Measure, _original.EffectiveUntil, ruling, clock);

    public SafetyResult<ModerationAction> Overturn(Guid reviewerId, AppealRuling ruling, TimeProvider clock) =>
        Decide(reviewerId, AppealState.Overturned, ModerationMeasure.Cleared, effectiveUntil: null, ruling, clock);

    private SafetyResult<ModerationAction> Decide(
        Guid reviewerId,
        AppealState outcome,
        ModerationMeasure measure,
        DateTimeOffset? effectiveUntil,
        AppealRuling ruling,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (State != AppealState.UnderReview)
        {
            return SafetyResult.Rejected<ModerationAction>(SafetyRejection.TransitionNotAllowed);
        }

        if (reviewerId != ReviewerId)
        {
            return SafetyResult.Rejected<ModerationAction>(SafetyRejection.OnlyAssignedReviewerCanDecide);
        }

        var now = clock.GetUtcNow();
        var revision = _original.Revise(measure, effectiveUntil, ruling, reviewerId, now);
        DecidedAt = now;
        State = outcome;
        return SafetyResult.Success(revision);
    }
}
