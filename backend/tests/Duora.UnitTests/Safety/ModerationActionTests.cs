using Duora.Core.Safety;
using static Duora.UnitTests.Safety.SafetyFixtures;

namespace Duora.UnitTests.Safety;

public sealed class ModerationActionTests
{
    [Fact]
    public void ADecisionRecordsEverythingNeededToTraceAndReviewIt()
    {
        var action = ModerationAction.DecideWithoutReport(Bruno, Decision(), FixedClock.AtStart()).Accepted();

        Assert.Equal(7, action.Id.Value.Version);
        Assert.Equal(Bruno, action.SubjectUserId);
        Assert.Null(action.ReportId);
        Assert.Equal(ModerationMeasure.Suspension, action.Measure);
        Assert.Equal(Policy, action.PolicyVersion);
        Assert.Equal([Evidence], action.EvidenceIds);
        Assert.Equal("Assédio reiterado na sala.", action.Reason);
        Assert.Equal(new ModerationAuthor.Human(ModeratorMaria), action.Author);
        Assert.Equal(Start, action.DecidedAt);
        Assert.Equal(Start + OneWeek, action.EffectiveUntil);
        Assert.Null(action.SupersedesActionId);
        Assert.True(action.IsReviewable);
    }

    [Fact]
    public void ThePermanentBanProposedByTheSystemIsRejected()
    {
        var violation = Assert.Throws<SafetyRuleViolationException>(() => ModerationAction.DecideWithoutReport(
            Bruno, Decision(ModerationMeasure.PermanentBan, new ModerationAuthor.Automated()), FixedClock.AtStart()));

        Assert.Equal(SafetyRule.PermanentSanctionRequiresHumanAuthor, violation.Rule);
    }

    [Fact]
    public void AHumanCanDecideAPermanentBanWithoutEndDate()
    {
        var action = ModerationAction.DecideWithoutReport(
            Bruno, Decision(ModerationMeasure.PermanentBan), FixedClock.AtStart()).Accepted();

        Assert.Equal(ModerationMeasure.PermanentBan, action.Measure);
        Assert.Null(action.EffectiveUntil);
    }

    [Fact]
    public void ClearedIsOnlyTheOutcomeOfAnAppeal()
    {
        var violation = Assert.Throws<SafetyRuleViolationException>(() => ModerationAction.DecideWithoutReport(
            Bruno, Decision(ModerationMeasure.Cleared), FixedClock.AtStart()));

        Assert.Equal(SafetyRule.ClearedIsOnlyAnAppealOutcome, violation.Rule);
    }

    [Theory]
    [InlineData(ModerationMeasure.Restriction, null)]
    [InlineData(ModerationMeasure.Suspension, null)]
    [InlineData(ModerationMeasure.Suspension, 0L)]
    [InlineData(ModerationMeasure.Suspension, -1L)]
    [InlineData(ModerationMeasure.Warning, 1L)]
    [InlineData(ModerationMeasure.PermanentBan, 1L)]
    public void TemporaryMeasuresNeedAPositiveDurationAndOthersNone(ModerationMeasure measure, long? durationTicks)
    {
        var decision = Decision(measure) with { Duration = durationTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null };

        var result = ModerationAction.DecideWithoutReport(Bruno, decision, FixedClock.AtStart());

        SafetyAssert.Rejected(SafetyRejection.MeasureDurationMismatch, result);
    }

    [Fact]
    public void ASuspensionOfOneTickIsAccepted()
    {
        var decision = Decision(ModerationMeasure.Suspension) with { Duration = TimeSpan.FromTicks(1) };

        var action = ModerationAction.DecideWithoutReport(Bruno, decision, FixedClock.AtStart()).Accepted();

        Assert.Equal(Start.AddTicks(1), action.EffectiveUntil);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADecisionWithoutEvidenceIsRejected(bool withEmptyId)
    {
        var decision = Decision() with { EvidenceIds = withEmptyId ? [Evidence, Guid.Empty] : [] };

        var violation = Assert.Throws<SafetyRuleViolationException>(
            () => ModerationAction.DecideWithoutReport(Bruno, decision, FixedClock.AtStart()));

        Assert.Equal(SafetyRule.DecisionRequiresEvidence, violation.Rule);
    }

    [Fact]
    public void EvidenceIdsAreCopiedSoTheCallerCannotRewriteThem()
    {
        var evidence = new List<Guid> { Evidence };
        var action = ModerationAction.DecideWithoutReport(
            Bruno, Decision() with { EvidenceIds = evidence }, FixedClock.AtStart()).Accepted();

        evidence.Add(Guid.NewGuid());

        Assert.Equal([Evidence], action.EvidenceIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ADecisionWithoutReasonIsRejected(string reason)
    {
        var decision = Decision() with { Reason = reason };

        Assert.Throws<ArgumentException>(
            () => ModerationAction.DecideWithoutReport(Bruno, decision, FixedClock.AtStart()));
    }

    [Theory]
    [InlineData("", "2026-10")]
    [InlineData("conduta", " ")]
    public void PolicyVersionNeedsCodeAndVersion(string code, string version)
    {
        Assert.Throws<ArgumentException>(() => new PolicyVersion(code, version));
    }

    [Fact]
    public void TheSubjectSeesOnlyTheNoticeFieldsOfTheirSanction()
    {
        var action = ModerationAction.DecideWithoutReport(Bruno, Decision(), FixedClock.AtStart()).Accepted();

        var notice = action.GetSanctionNotice(Bruno);

        var keys = typeof(SanctionNoticeView).GetProperties().Select(property => property.Name).Order();
        Assert.Equal(
            ["ActionId", "CanAppeal", "DecidedAt", "DecidedAutomatically", "EffectiveUntil", "Measure", "PolicyVersion", "Reason"],
            keys);
        Assert.Equal(
            new SanctionNoticeView(
                action.Id,
                ModerationMeasure.Suspension,
                "Assédio reiterado na sala.",
                Policy,
                Start,
                Start + OneWeek,
                DecidedAutomatically: false,
                CanAppeal: true),
            notice);
    }

    [Fact]
    public void TheNoticeSaysWhenTheDecisionWasAutomated()
    {
        var action = ModerationAction.DecideWithoutReport(
            Bruno, Decision(ModerationMeasure.Warning, new ModerationAuthor.Automated()), FixedClock.AtStart()).Accepted();

        Assert.True(action.GetSanctionNotice(Bruno)?.DecidedAutomatically);
    }

    [Theory]
    [InlineData("reporter")]
    [InlineData("moderator")]
    public void NobodyButTheSubjectSeesTheSanctionNotice(string requester)
    {
        var action = ModerationAction.DecideWithoutReport(Bruno, Decision(), FixedClock.AtStart()).Accepted();

        Assert.Null(action.GetSanctionNotice(requester == "reporter" ? Alice : ModeratorMaria));
    }
}
