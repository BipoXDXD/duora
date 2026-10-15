using Duora.Core.Safety;
using static Duora.UnitTests.Safety.SafetyFixtures;

namespace Duora.UnitTests.Safety;

public sealed class AppealTests
{
    [Fact]
    public void TheSanctionedPersonFilesAnAppealOnTheirOwnMeasure()
    {
        var clock = FixedClock.AtStart();
        var suspension = SanctionOn(Bruno, clock: clock);

        var appeal = Appeal.File(suspension, Bruno, "Foi um mal-entendido.", clock).Accepted();

        Assert.Equal(7, appeal.Id.Value.Version);
        Assert.Equal(suspension.Id, appeal.ModerationActionId);
        Assert.Equal(Bruno, appeal.AppellantId);
        Assert.Equal("Foi um mal-entendido.", appeal.Reason);
        Assert.Equal(AppealState.Received, appeal.State);
        Assert.Equal(Start, appeal.FiledAt);
        Assert.Null(appeal.ReviewerId);
    }

    [Theory]
    [InlineData("reporter")]
    [InlineData("stranger")]
    public void NobodyCanAppealSomeoneElsesSanction(string appellant)
    {
        var suspension = SanctionOn(Bruno);

        var violation = Assert.Throws<SafetyRuleViolationException>(
            () => Appeal.File(suspension, appellant == "reporter" ? Alice : Carla, "Injusto.", FixedClock.AtStart()));

        Assert.Equal(SafetyRule.AppealOnlyOwnSanction, violation.Rule);
    }

    [Fact]
    public void TheDecisionOfAnAppealCannotBeAppealedAgain()
    {
        var clock = FixedClock.AtStart();
        var revision = UpheldRevision(clock);

        SafetyAssert.Rejected(SafetyRejection.ActionNotAppealable, Appeal.File(revision, Bruno, "De novo.", clock));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Appeal.ReasonMaxLength + 1)]
    public void AppealReasonMustBeBetweenOneCharacterAndTheMaximum(int length)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => Appeal.File(SanctionOn(Bruno), Bruno, new string('a', length), FixedClock.AtStart()));
    }

    [Fact]
    public void AppealReasonAtTheMaximumIsAccepted()
    {
        var reason = new string('a', Appeal.ReasonMaxLength);

        Assert.Equal(reason, Appeal.File(SanctionOn(Bruno), Bruno, reason, FixedClock.AtStart()).Accepted().Reason);
    }

    [Fact]
    public void TheAuthorOfTheOriginalDecisionCannotReviewTheAppeal()
    {
        var appeal = Appeal.File(SanctionOn(Bruno), Bruno, "Injusto.", FixedClock.AtStart()).Accepted();

        SafetyAssert.Rejected(SafetyRejection.ReviewerMustBeIndependent, appeal.StartReview(ModeratorMaria));
        Assert.Equal(AppealState.Received, appeal.State);
    }

    [Fact]
    public void TheAppellantCannotReviewTheirOwnAppeal()
    {
        var appeal = Appeal.File(SanctionOn(Bruno), Bruno, "Injusto.", FixedClock.AtStart()).Accepted();

        SafetyAssert.Rejected(SafetyRejection.ReviewerMustBeIndependent, appeal.StartReview(Bruno));
    }

    [Fact]
    public void AnyHumanCanReviewAnAutomatedDecision()
    {
        var clock = FixedClock.AtStart();
        var warning = ModerationAction.DecideWithoutReport(
            Bruno, Decision(ModerationMeasure.Warning, new ModerationAuthor.Automated()), clock).Accepted();
        var appeal = Appeal.File(warning, Bruno, "Injusto.", clock).Accepted();

        appeal.StartReview(ModeratorMaria).Accepted();

        Assert.Equal((AppealState.UnderReview, ModeratorMaria), (appeal.State, appeal.ReviewerId));
    }

    [Fact]
    public void UpholdCreatesANewRevisionAndKeepsTheOriginalDecisionIntact()
    {
        var clock = FixedClock.AtStart();
        var original = SanctionOn(Bruno, clock: clock);
        var appeal = AppealUnderReview(original, clock);
        clock.Advance(TimeSpan.FromDays(1));

        var revision = appeal.Uphold(ModeratorPedro, "Evidência confirmada.", RevisedPolicy, clock).Accepted();

        Assert.Equal(AppealState.Upheld, appeal.State);
        Assert.Equal(Start.AddDays(1), appeal.DecidedAt);
        Assert.NotEqual(original.Id, revision.Id);
        Assert.Equal(original.Id, revision.SupersedesActionId);
        Assert.Equal(ModerationMeasure.Suspension, revision.Measure);
        Assert.Equal(original.EffectiveUntil, revision.EffectiveUntil);
        Assert.Equal(Bruno, revision.SubjectUserId);
        Assert.Equal(RevisedPolicy, revision.PolicyVersion);
        Assert.Equal(original.EvidenceIds, revision.EvidenceIds);
        Assert.Equal("Evidência confirmada.", revision.Reason);
        Assert.Equal(new ModerationAuthor.Human(ModeratorPedro), revision.Author);
        Assert.Equal(Start.AddDays(1), revision.DecidedAt);
        Assert.False(revision.IsReviewable);
        AssertOriginalUnchanged(original);
    }

    [Fact]
    public void OverturnCreatesAClearedRevisionAndKeepsTheOriginalDecisionIntact()
    {
        var clock = FixedClock.AtStart();
        var original = SanctionOn(Bruno, clock: clock);
        var appeal = AppealUnderReview(original, clock);

        var revision = appeal.Overturn(ModeratorPedro, "Evidência insuficiente.", RevisedPolicy, clock).Accepted();

        Assert.Equal(AppealState.Overturned, appeal.State);
        Assert.Equal(original.Id, revision.SupersedesActionId);
        Assert.Equal(ModerationMeasure.Cleared, revision.Measure);
        Assert.Null(revision.EffectiveUntil);
        Assert.False(revision.IsReviewable);
        AssertOriginalUnchanged(original);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyTheAssignedReviewerCanDecide(bool uphold)
    {
        var clock = FixedClock.AtStart();
        var appeal = AppealUnderReview(SanctionOn(Bruno), clock);

        var result = uphold
            ? appeal.Uphold(Carla, "Mantida.", RevisedPolicy, clock)
            : appeal.Overturn(Carla, "Revertida.", RevisedPolicy, clock);

        SafetyAssert.Rejected(SafetyRejection.OnlyAssignedReviewerCanDecide, result);
        Assert.Equal(AppealState.UnderReview, appeal.State);
    }

    [Theory]
    [InlineData(AppealState.UnderReview)]
    [InlineData(AppealState.Upheld)]
    [InlineData(AppealState.Overturned)]
    public void StartReviewIsOnlyValidForAReceivedAppeal(AppealState state)
    {
        var appeal = AppealIn(state);

        SafetyAssert.Rejected(SafetyRejection.TransitionNotAllowed, appeal.StartReview(Carla));
        Assert.Equal(state, appeal.State);
    }

    [Theory]
    [InlineData(AppealState.Received, true)]
    [InlineData(AppealState.Upheld, true)]
    [InlineData(AppealState.Overturned, true)]
    [InlineData(AppealState.Received, false)]
    [InlineData(AppealState.Upheld, false)]
    [InlineData(AppealState.Overturned, false)]
    public void DecisionIsOnlyValidForAnAppealUnderReview(AppealState state, bool uphold)
    {
        var clock = FixedClock.AtStart();
        var appeal = AppealIn(state);

        var result = uphold
            ? appeal.Uphold(ModeratorPedro, "Mantida.", RevisedPolicy, clock)
            : appeal.Overturn(ModeratorPedro, "Revertida.", RevisedPolicy, clock);

        SafetyAssert.Rejected(SafetyRejection.TransitionNotAllowed, result);
        Assert.Equal(state, appeal.State);
    }

    [Fact]
    public void TheNoticeOfARevisionSaysItCannotBeAppealedAgain()
    {
        var revision = UpheldRevision(FixedClock.AtStart());

        Assert.False(revision.GetSanctionNotice(Bruno)?.CanAppeal);
    }

    private static void AssertOriginalUnchanged(ModerationAction original)
    {
        Assert.Equal(ModerationMeasure.Suspension, original.Measure);
        Assert.Equal(Policy, original.PolicyVersion);
        Assert.Equal("Assédio reiterado na sala.", original.Reason);
        Assert.Equal(new ModerationAuthor.Human(ModeratorMaria), original.Author);
        Assert.Equal(Start, original.DecidedAt);
        Assert.Equal(Start + OneWeek, original.EffectiveUntil);
        Assert.True(original.IsReviewable);
    }

    private static ModerationAction UpheldRevision(TimeProvider clock) =>
        AppealUnderReview(SanctionOn(Bruno, clock: clock), clock)
            .Uphold(ModeratorPedro, "Mantida.", RevisedPolicy, clock)
            .Accepted();

    private static Appeal AppealIn(AppealState state)
    {
        var clock = FixedClock.AtStart();
        if (state == AppealState.Received)
        {
            return Appeal.File(SanctionOn(Bruno), Bruno, "Injusto.", clock).Accepted();
        }

        var appeal = AppealUnderReview(SanctionOn(Bruno), clock);
        if (state == AppealState.Upheld)
        {
            appeal.Uphold(ModeratorPedro, "Mantida.", RevisedPolicy, clock).Accepted();
        }
        else if (state == AppealState.Overturned)
        {
            appeal.Overturn(ModeratorPedro, "Revertida.", RevisedPolicy, clock).Accepted();
        }

        return appeal;
    }
}
