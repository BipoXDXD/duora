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

        var appeal = Appeal.File(suspension, Bruno, "Foi um mal-entendido.", existingAppeal: null, clock).Accepted();

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
            () => Appeal.File(suspension, appellant == "reporter" ? Alice : Carla, "Injusto.", existingAppeal: null, FixedClock.AtStart()));

        Assert.Equal(SafetyRule.AppealOnlyOwnSanction, violation.Rule);
    }

    [Fact]
    public void TheDecisionOfAnAppealCannotBeAppealedAgain()
    {
        var clock = FixedClock.AtStart();
        var revision = UpheldRevision(clock);

        SafetyAssert.Rejected(SafetyRejection.ActionNotAppealable, Appeal.File(revision, Bruno, "De novo.", existingAppeal: null, clock));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheOriginalMeasureAcceptsNoSecondAppealOnceDecided(bool upheld)
    {
        var clock = FixedClock.AtStart();
        var original = SanctionOn(Bruno, clock: clock);
        var first = AppealUnderReview(original, clock);
        _ = upheld
            ? first.Uphold(original, ModeratorPedro, Ruling("Mantida."), clock).Accepted()
            : first.Overturn(original, ModeratorPedro, Ruling("Revertida."), clock).Accepted();

        var second = Appeal.File(original, Bruno, "De novo.", existingAppeal: first, clock);

        SafetyAssert.Rejected(SafetyRejection.ActionNotAppealable, second);
    }

    [Fact]
    public void TheOriginalMeasureAcceptsNoSecondAppealWhileTheFirstIsOpen()
    {
        var clock = FixedClock.AtStart();
        var original = SanctionOn(Bruno, clock: clock);
        var first = Appeal.File(original, Bruno, "Injusto.", existingAppeal: null, clock).Accepted();

        var second = Appeal.File(original, Bruno, "De novo.", existingAppeal: first, clock);

        SafetyAssert.Rejected(SafetyRejection.ActionNotAppealable, second);
    }

    [Fact]
    public void TheNoticeOffersAnAppealOnlyWhileNoneWasFiled()
    {
        var clock = FixedClock.AtStart();
        var original = SanctionOn(Bruno, clock: clock);
        var appeal = Appeal.File(original, Bruno, "Injusto.", existingAppeal: null, clock).Accepted();

        Assert.True(original.GetSanctionNotice(Bruno, existingAppeal: null)?.CanAppeal);
        Assert.False(original.GetSanctionNotice(Bruno, existingAppeal: appeal)?.CanAppeal);
    }

    [Fact]
    public void AnExistingAppealOfAnotherMeasureIsAProgrammingError()
    {
        var clock = FixedClock.AtStart();
        var original = SanctionOn(Bruno, clock: clock);
        var unrelated = Appeal.File(SanctionOn(Bruno), Bruno, "Outro.", existingAppeal: null, clock).Accepted();

        Assert.Throws<ArgumentException>(() => Appeal.File(original, Bruno, "De novo.", existingAppeal: unrelated, clock));
        Assert.Throws<ArgumentException>(() => original.GetSanctionNotice(Bruno, existingAppeal: unrelated));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecidingWithADifferentMeasureThanTheAppealedOneIsAProgrammingError(bool uphold)
    {
        var clock = FixedClock.AtStart();
        var appeal = AppealUnderReview(SanctionOn(Bruno), clock);
        var other = SanctionOn(Bruno);

        Assert.Throws<ArgumentException>(() => uphold
            ? appeal.Uphold(other, ModeratorPedro, Ruling("Mantida."), clock)
            : appeal.Overturn(other, ModeratorPedro, Ruling("Revertida."), clock));
        Assert.Equal(AppealState.UnderReview, appeal.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Appeal.ReasonMaxLength + 1)]
    public void AppealReasonMustBeBetweenOneCharacterAndTheMaximum(int length)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => Appeal.File(SanctionOn(Bruno), Bruno, new string('a', length), existingAppeal: null, FixedClock.AtStart()));
    }

    [Fact]
    public void AppealReasonAtTheMaximumIsAccepted()
    {
        var reason = new string('a', Appeal.ReasonMaxLength);

        Assert.Equal(reason, Appeal.File(SanctionOn(Bruno), Bruno, reason, existingAppeal: null, FixedClock.AtStart()).Accepted().Reason);
    }

    [Fact]
    public void TheAuthorOfTheOriginalDecisionCannotReviewTheAppeal()
    {
        var appeal = Appeal.File(SanctionOn(Bruno), Bruno, "Injusto.", existingAppeal: null, FixedClock.AtStart()).Accepted();

        SafetyAssert.Rejected(SafetyRejection.ReviewerMustBeIndependent, appeal.StartReview(ModeratorMaria));
        Assert.Equal(AppealState.Received, appeal.State);
    }

    [Theory]
    [InlineData("reporter")]
    [InlineData("target")]
    public void APartyToTheOriginalReportCannotReviewTheAppeal(string party)
    {
        var clock = FixedClock.AtStart();
        var report = ReportIn(ReportState.UnderReview, clock);
        var action = report.TakeAction(Decision(), Limits, clock).Accepted();
        var appeal = Appeal.File(action, Bruno, "Injusto.", existingAppeal: null, clock).Accepted();

        var result = appeal.StartReview(party == "reporter" ? Alice : Bruno);

        SafetyAssert.Rejected(SafetyRejection.ReviewerMustBeIndependent, result);
        Assert.Equal(AppealState.Received, appeal.State);
    }

    [Fact]
    public void TheMeasureCarriesThePartiesOfItsReport()
    {
        var clock = FixedClock.AtStart();
        var report = ReportIn(ReportState.UnderReview, clock);

        var action = report.TakeAction(Decision(), Limits, clock).Accepted();

        Assert.Equal(new CaseParties(Bruno, Alice), action.Parties);
        Assert.Equal(new CaseParties(Bruno, null), SanctionOn(Bruno).Parties);
    }

    [Fact]
    public void TheAppellantCannotReviewTheirOwnAppeal()
    {
        var appeal = Appeal.File(SanctionOn(Bruno), Bruno, "Injusto.", existingAppeal: null, FixedClock.AtStart()).Accepted();

        SafetyAssert.Rejected(SafetyRejection.ReviewerMustBeIndependent, appeal.StartReview(Bruno));
    }

    [Fact]
    public void AnyHumanCanReviewAnAutomatedDecision()
    {
        var clock = FixedClock.AtStart();
        var restriction = ModerationAction.DecideWithoutReport(
            Bruno, Decision(ModerationMeasure.Restriction, new ModerationAuthor.Automated()), Limits, clock).Accepted();
        var appeal = Appeal.File(restriction, Bruno, "Injusto.", existingAppeal: null, clock).Accepted();

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

        var revision = appeal.Uphold(original, ModeratorPedro, Ruling("Evidência confirmada."), clock).Accepted();

        Assert.Equal(AppealState.Upheld, appeal.State);
        Assert.Equal(Start.AddDays(1), appeal.DecidedAt);
        Assert.NotEqual(original.Id, revision.Id);
        Assert.Equal(original.Id, revision.SupersedesActionId);
        Assert.Equal(ModerationMeasure.Suspension, revision.Measure);
        Assert.Equal(original.EffectiveUntil, revision.EffectiveUntil);
        Assert.Equal(Bruno, revision.SubjectUserId);
        Assert.Equal(RevisedPolicy, revision.PolicyVersion);
        Assert.Equal(original.EvidenceIds, revision.EvidenceIds);
        Assert.Equal("Evidência confirmada.", revision.InternalReason);
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

        var revision = appeal.Overturn(original, ModeratorPedro, Ruling("Evidência insuficiente."), clock).Accepted();

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
        var original = SanctionOn(Bruno);
        var appeal = AppealUnderReview(original, clock);

        var result = uphold
            ? appeal.Uphold(original, Carla, Ruling("Mantida."), clock)
            : appeal.Overturn(original, Carla, Ruling("Revertida."), clock);

        SafetyAssert.Rejected(SafetyRejection.OnlyAssignedReviewerCanDecide, result);
        Assert.Equal(AppealState.UnderReview, appeal.State);
    }

    [Theory]
    [InlineData(AppealState.UnderReview)]
    [InlineData(AppealState.Upheld)]
    [InlineData(AppealState.Overturned)]
    public void StartReviewIsOnlyValidForAReceivedAppeal(AppealState state)
    {
        var (appeal, _) = AppealIn(state);

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
        var (appeal, original) = AppealIn(state);

        var result = uphold
            ? appeal.Uphold(original, ModeratorPedro, Ruling("Mantida."), clock)
            : appeal.Overturn(original, ModeratorPedro, Ruling("Revertida."), clock);

        SafetyAssert.Rejected(SafetyRejection.TransitionNotAllowed, result);
        Assert.Equal(state, appeal.State);
    }

    [Fact]
    public void TheNoticeOfARevisionSaysItCannotBeAppealedAgain()
    {
        var revision = UpheldRevision(FixedClock.AtStart());

        Assert.False(revision.GetSanctionNotice(Bruno, existingAppeal: null)?.CanAppeal);
    }

    private static void AssertOriginalUnchanged(ModerationAction original)
    {
        Assert.Equal(ModerationMeasure.Suspension, original.Measure);
        Assert.Equal(Policy, original.PolicyVersion);
        Assert.Equal("Assédio reiterado na sala.", original.InternalReason);
        Assert.Equal(new ModerationAuthor.Human(ModeratorMaria), original.Author);
        Assert.Equal(Start, original.DecidedAt);
        Assert.Equal(Start + OneWeek, original.EffectiveUntil);
        Assert.True(original.IsReviewable);
    }

    private static ModerationAction UpheldRevision(TimeProvider clock) => Upheld(SanctionOn(Bruno, clock: clock), clock);

    private static (Appeal Appeal, ModerationAction Original) AppealIn(AppealState state)
    {
        var clock = FixedClock.AtStart();
        var original = SanctionOn(Bruno);
        if (state == AppealState.Received)
        {
            return (Appeal.File(original, Bruno, "Injusto.", existingAppeal: null, clock).Accepted(), original);
        }

        var appeal = AppealUnderReview(original, clock);
        if (state == AppealState.Upheld)
        {
            appeal.Uphold(original, ModeratorPedro, Ruling("Mantida."), clock).Accepted();
        }
        else if (state == AppealState.Overturned)
        {
            appeal.Overturn(original, ModeratorPedro, Ruling("Revertida."), clock).Accepted();
        }

        return (appeal, original);
    }
}
