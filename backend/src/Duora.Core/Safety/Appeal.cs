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
/// da decisão original; a medida recorrida não muda nem some (spec 4.2, 12.3). Há um só recurso por medida.
/// </summary>
/// <remarks>
/// Guarda o id da medida e só o que a independência precisa (autor e partes). Para decidir, recebe a medida original
/// de novo, porque a revisão copia dela evidência, denúncia e fim.
/// </remarks>
public sealed class Appeal
{
    /// <summary>Em caracteres Unicode; mesmo teto do `reason` em safety.appeals.</summary>
    public const int ReasonMaxLength = 2000;

    private readonly ModerationAuthor _originalAuthor;
    private readonly CaseParties _parties;

    private Appeal(AppealId id, ModerationAction original, string reason, DateTimeOffset filedAt)
    {
        Id = id;
        ModerationActionId = original.Id;
        _originalAuthor = original.Author;
        _parties = original.Parties;
        Reason = reason;
        FiledAt = filedAt;
        State = AppealState.Received;
    }

    public AppealId Id { get; }

    public ModerationActionId ModerationActionId { get; }

    public Guid AppellantId => _parties.SubjectUserId;

    public string Reason { get; }

    public DateTimeOffset FiledAt { get; }

    public AppealState State { get; private set; }

    public Guid? ReviewerId { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <param name="action">Medida recorrida.</param>
    /// <param name="appellantId">Quem recorre, vindo da sessão.</param>
    /// <param name="reason">Motivo do recurso.</param>
    /// <param name="existingAppeal">
    /// O recurso já registrado para esta medida, aberto ou decidido, se houver. Vem do banco, onde o índice único em
    /// `appeals(moderation_action_id)` garante no máximo um.
    /// </param>
    /// <param name="clock">Relógio do registro.</param>
    public static SafetyResult<Appeal> File(
        ModerationAction action,
        Guid appellantId,
        string reason,
        Appeal? existingAppeal,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(clock);
        if (appellantId != action.SubjectUserId)
        {
            throw new SafetyRuleViolationException(SafetyRule.AppealOnlyOwnSanction);
        }

        var validReason = SafetyText.RequireText(reason, ReasonMaxLength, nameof(reason));
        if (!action.AcceptsAppeal(existingAppeal))
        {
            return SafetyResult.Rejected<Appeal>(SafetyRejection.ActionNotAppealable);
        }

        var now = clock.GetUtcNow();
        return SafetyResult.Success(new Appeal(AppealId.New(now), action, validReason, now));
    }

    /// <summary>Recurso é revisão humana; não repete o autor da decisão nem envolve as partes do caso.</summary>
    public SafetyResult StartReview(Guid reviewerId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(reviewerId, Guid.Empty);
        if (State != AppealState.Received)
        {
            return SafetyResult.Rejected(SafetyRejection.TransitionNotAllowed);
        }

        if (_originalAuthor.Is(reviewerId) || _parties.Includes(reviewerId))
        {
            return SafetyResult.Rejected(SafetyRejection.ReviewerMustBeIndependent);
        }

        ReviewerId = reviewerId;
        State = AppealState.UnderReview;
        return SafetyResult.Success();
    }

    /// <summary>Mantém a medida: a revisão repete medida e fim, com a fundamentação do revisor.</summary>
    public SafetyResult<ModerationAction> Uphold(
        ModerationAction original,
        Guid reviewerId,
        AppealRuling ruling,
        TimeProvider clock) =>
        Decide(original, reviewerId, AppealState.Upheld, ruling, clock);

    public SafetyResult<ModerationAction> Overturn(
        ModerationAction original,
        Guid reviewerId,
        AppealRuling ruling,
        TimeProvider clock) =>
        Decide(original, reviewerId, AppealState.Overturned, ruling, clock);

    private SafetyResult<ModerationAction> Decide(
        ModerationAction original,
        Guid reviewerId,
        AppealState outcome,
        AppealRuling ruling,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(clock);
        if (original.Id != ModerationActionId)
        {
            throw new ArgumentException("A medida não é a recorrida.", nameof(original));
        }

        if (State != AppealState.UnderReview)
        {
            return SafetyResult.Rejected<ModerationAction>(SafetyRejection.TransitionNotAllowed);
        }

        if (reviewerId != ReviewerId)
        {
            return SafetyResult.Rejected<ModerationAction>(SafetyRejection.OnlyAssignedReviewerCanDecide);
        }

        var now = clock.GetUtcNow();
        var revision = outcome == AppealState.Upheld
            ? original.Revise(original.Measure, original.EffectiveUntil, ruling, reviewerId, now)
            : original.Revise(ModerationMeasure.Cleared, effectiveUntil: null, ruling, reviewerId, now);
        DecidedAt = now;
        State = outcome;
        return SafetyResult.Success(revision);
    }
}
