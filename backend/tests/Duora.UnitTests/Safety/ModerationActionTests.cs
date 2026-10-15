using Duora.Core.Safety;
using static Duora.UnitTests.Safety.SafetyFixtures;

namespace Duora.UnitTests.Safety;

public sealed class ModerationActionTests
{
    [Fact]
    public void ADecisionRecordsEverythingNeededToTraceAndReviewIt()
    {
        var action = ModerationAction.DecideWithoutReport(Bruno, Decision(), Limits, FixedClock.AtStart()).Accepted();

        Assert.Equal(7, action.Id.Value.Version);
        Assert.Equal(Bruno, action.SubjectUserId);
        Assert.Null(action.ReportId);
        Assert.Equal(ModerationMeasure.Suspension, action.Measure);
        Assert.Equal(Policy, action.PolicyVersion);
        Assert.Equal([Evidence], action.EvidenceIds);
        Assert.Equal("Assédio reiterado na sala.", action.InternalReason);
        Assert.Equal(new ModerationAuthor.Human(ModeratorMaria), action.Author);
        Assert.Equal(Start, action.DecidedAt);
        Assert.Equal(Start + OneWeek, action.EffectiveUntil);
        Assert.Null(action.SupersedesActionId);
        Assert.True(action.IsReviewable);
    }

    [Theory]
    [InlineData(ModerationMeasure.Warning)]
    [InlineData(ModerationMeasure.Suspension)]
    [InlineData(ModerationMeasure.PermanentBan)]
    public void TheSystemCanOnlyApplyARestriction(ModerationMeasure measure)
    {
        var decision = Decision(measure, new ModerationAuthor.Automated());

        var violation = Assert.Throws<SafetyRuleViolationException>(
            () => ModerationAction.DecideWithoutReport(Bruno, decision, Limits, FixedClock.AtStart()));

        Assert.Equal(SafetyRule.MeasureRequiresHumanAuthor, violation.Rule);
    }

    [Fact]
    public void TheSystemCanRestrictUpToTheAutomatedCeiling()
    {
        var decision = Decision(ModerationMeasure.Restriction, new ModerationAuthor.Automated(), Limits.MaxAutomatedDuration);

        var action = ModerationAction.DecideWithoutReport(Bruno, decision, Limits, FixedClock.AtStart()).Accepted();

        Assert.Equal(ModerationMeasure.Restriction, action.Measure);
        Assert.Equal(Start + Limits.MaxAutomatedDuration, action.EffectiveUntil);
        Assert.Equal(new ModerationAuthor.Automated(), action.Author);
    }

    [Fact]
    public void AnAutomatedRestrictionOneTickAboveTheCeilingIsRejected()
    {
        var duration = Limits.MaxAutomatedDuration + TimeSpan.FromTicks(1);
        var decision = Decision(ModerationMeasure.Restriction, new ModerationAuthor.Automated(), duration);

        var result = ModerationAction.DecideWithoutReport(Bruno, decision, Limits, FixedClock.AtStart());

        SafetyAssert.Rejected(SafetyRejection.DurationExceedsLimit, result);
    }

    [Theory]
    [InlineData(ModerationMeasure.Restriction)]
    [InlineData(ModerationMeasure.Suspension)]
    public void AHumanCanApplyATemporaryMeasureUpToTheMaximumDuration(ModerationMeasure measure)
    {
        var action = ModerationAction.DecideWithoutReport(
            Bruno, Decision(measure, duration: Limits.MaxDuration), Limits, FixedClock.AtStart()).Accepted();

        Assert.Equal(Start + Limits.MaxDuration, action.EffectiveUntil);
    }

    [Fact]
    public void AHumanMeasureOneTickAboveTheMaximumDurationIsRejected()
    {
        var decision = Decision(ModerationMeasure.Suspension, duration: Limits.MaxDuration + TimeSpan.FromTicks(1));

        var result = ModerationAction.DecideWithoutReport(Bruno, decision, Limits, FixedClock.AtStart());

        SafetyAssert.Rejected(SafetyRejection.DurationExceedsLimit, result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLargestPossibleDurationIsRejectedWithoutOverflow(bool automated)
    {
        ModerationAuthor author = automated ? new ModerationAuthor.Automated() : new ModerationAuthor.Human(ModeratorMaria);
        var decision = Decision(ModerationMeasure.Restriction, author, TimeSpan.MaxValue);

        var result = ModerationAction.DecideWithoutReport(Bruno, decision, Limits, FixedClock.AtStart());

        SafetyAssert.Rejected(SafetyRejection.DurationExceedsLimit, result);
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(91, 90)]
    [InlineData(1, ModerationLimits.AbsoluteMaxDays + 1)]
    public void LimitsMustBePositiveOrderedAndUnderTheAbsoluteCeiling(int automatedDays, int maxDays)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ModerationLimits(TimeSpan.FromDays(automatedDays), TimeSpan.FromDays(maxDays)));
    }

    [Fact]
    public void LimitsAcceptTheAbsoluteCeiling()
    {
        var ceiling = TimeSpan.FromDays(ModerationLimits.AbsoluteMaxDays);

        Assert.Equal(ceiling, new ModerationLimits(ceiling, ceiling).MaxDuration);
    }

    [Fact]
    public void AHumanCanDecideAPermanentBanWithoutEndDate()
    {
        var action = ModerationAction.DecideWithoutReport(
            Bruno, Decision(ModerationMeasure.PermanentBan), Limits, FixedClock.AtStart()).Accepted();

        Assert.Equal(ModerationMeasure.PermanentBan, action.Measure);
        Assert.Null(action.EffectiveUntil);
    }

    [Fact]
    public void ClearedIsOnlyTheOutcomeOfAnAppeal()
    {
        var violation = Assert.Throws<SafetyRuleViolationException>(() => ModerationAction.DecideWithoutReport(
            Bruno, Decision(ModerationMeasure.Cleared), Limits, FixedClock.AtStart()));

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

        var result = ModerationAction.DecideWithoutReport(Bruno, decision, Limits, FixedClock.AtStart());

        SafetyAssert.Rejected(SafetyRejection.MeasureDurationMismatch, result);
    }

    [Fact]
    public void ASuspensionOfOneTickIsAccepted()
    {
        var decision = Decision(ModerationMeasure.Suspension) with { Duration = TimeSpan.FromTicks(1) };

        var action = ModerationAction.DecideWithoutReport(Bruno, decision, Limits, FixedClock.AtStart()).Accepted();

        Assert.Equal(Start.AddTicks(1), action.EffectiveUntil);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADecisionWithoutEvidenceIsRejected(bool withEmptyId)
    {
        var decision = Decision() with { EvidenceIds = withEmptyId ? [Evidence, Guid.Empty] : [] };

        var violation = Assert.Throws<SafetyRuleViolationException>(
            () => ModerationAction.DecideWithoutReport(Bruno, decision, Limits, FixedClock.AtStart()));

        Assert.Equal(SafetyRule.DecisionRequiresEvidence, violation.Rule);
    }

    [Fact]
    public void EvidenceIdsAreCopiedSoTheCallerCannotRewriteThem()
    {
        var evidence = new List<Guid> { Evidence };
        var action = ModerationAction.DecideWithoutReport(
            Bruno, Decision() with { EvidenceIds = evidence }, Limits, FixedClock.AtStart()).Accepted();

        evidence.Add(Guid.NewGuid());

        Assert.Equal([Evidence], action.EvidenceIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ADecisionWithoutReasonIsRejected(string reason)
    {
        var decision = Decision() with { InternalReason = reason };

        Assert.Throws<ArgumentException>(
            () => ModerationAction.DecideWithoutReport(Bruno, decision, Limits, FixedClock.AtStart()));
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
        var action = ModerationAction.DecideWithoutReport(Bruno, Decision(), Limits, FixedClock.AtStart()).Accepted();

        var notice = action.GetSanctionNotice(Bruno);

        var keys = typeof(SanctionNoticeView).GetProperties().Select(property => property.Name).Order();
        Assert.Equal(
            [
                "ActionId", "CanAppeal", "DecidedAt", "DecidedAutomatically", "EffectiveUntil", "Measure",
                "NoticeText", "PolicyVersion", "ReasonCode",
            ],
            keys);
        Assert.Equal(
            new SanctionNoticeView(
                action.Id,
                ModerationMeasure.Suspension,
                "conduta.assedio",
                "Mensagens ofensivas repetidas na sala.",
                Policy,
                Start,
                Start + OneWeek,
                DecidedAutomatically: false,
                CanAppeal: true),
            notice);
    }

    [Fact]
    public void TheInternalReasonNeverReachesTheSanctionedPerson()
    {
        var canary = $"CANARY-{Guid.NewGuid()}";
        var clock = FixedClock.AtStart();
        var action = ModerationAction.DecideWithoutReport(
            Bruno, Decision() with { InternalReason = canary }, Limits, clock).Accepted();
        var revision = AppealUnderReview(action, clock).Overturn(ModeratorPedro, Ruling(canary), clock).Accepted();

        Assert.Equal(canary, action.InternalReason);
        Assert.DoesNotContain(canary, action.GetSanctionNotice(Bruno)?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(canary, revision.GetSanctionNotice(Bruno)?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Conduta")]
    [InlineData("conduta assedio")]
    [InlineData(".conduta")]
    public void NoticeReasonCodeMustBeACatalogCode(string code)
    {
        Assert.Throws<ArgumentException>(() => new SubjectNotice(code, null));
    }

    [Fact]
    public void NoticeReasonCodeIsLimitedInLength()
    {
        Assert.Equal(64, new SubjectNotice(new string('a', 64), null).ReasonCode.Length);
        Assert.Throws<ArgumentException>(() => new SubjectNotice(new string('a', 65), null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SubjectNotice.TextMaxLength)]
    public void NoticeTextIsOptionalUpToTheMaximum(int? length)
    {
        var text = length is { } size ? new string('a', size) : null;

        Assert.Equal(text, new SubjectNotice("conduta.assedio", text).Text);
    }

    [Fact]
    public void NoticeTextAboveTheMaximumIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SubjectNotice("conduta.assedio", new string('a', SubjectNotice.TextMaxLength + 1)));
    }

    [Fact]
    public void TheNoticeSaysWhenTheDecisionWasAutomated()
    {
        var action = ModerationAction.DecideWithoutReport(
            Bruno, Decision(ModerationMeasure.Restriction, new ModerationAuthor.Automated()), Limits, FixedClock.AtStart())
            .Accepted();

        Assert.True(action.GetSanctionNotice(Bruno)?.DecidedAutomatically);
    }

    [Theory]
    [InlineData("reporter")]
    [InlineData("moderator")]
    public void NobodyButTheSubjectSeesTheSanctionNotice(string requester)
    {
        var action = ModerationAction.DecideWithoutReport(Bruno, Decision(), Limits, FixedClock.AtStart()).Accepted();

        Assert.Null(action.GetSanctionNotice(requester == "reporter" ? Alice : ModeratorMaria));
    }
}
