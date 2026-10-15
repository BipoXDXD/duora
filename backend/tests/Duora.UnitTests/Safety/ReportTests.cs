using Duora.Core.Safety;
using static Duora.UnitTests.Safety.SafetyFixtures;

namespace Duora.UnitTests.Safety;

public sealed class ReportTests
{
    public enum Transition
    {
        Triage,
        StartReview,
        TakeAction,
        Close,
        MarkAppealed,
        ResolveAppeal,
    }

    private static readonly Dictionary<Transition, ReportState> ValidSource = new()
    {
        [Transition.Triage] = ReportState.Received,
        [Transition.StartReview] = ReportState.Triaged,
        [Transition.TakeAction] = ReportState.UnderReview,
        [Transition.Close] = ReportState.UnderReview,
        [Transition.MarkAppealed] = ReportState.Actioned,
        [Transition.ResolveAppeal] = ReportState.Appealed,
    };

    public static TheoryData<Transition, ReportState> InvalidTransitions()
    {
        var data = new TheoryData<Transition, ReportState>();
        foreach (var transition in Enum.GetValues<Transition>())
        {
            foreach (var state in Enum.GetValues<ReportState>().Where(state => state != ValidSource[transition]))
            {
                data.Add(transition, state);
            }
        }

        return data;
    }

    [Fact]
    public void SubmitReceivesTheReportWithAServerGeneratedProtocol()
    {
        var report = Report.Submit(Submission(), FixedClock.AtStart());

        Assert.Equal(ReportState.Received, report.State);
        Assert.Equal(7, report.Id.Value.Version);
        Assert.Equal(Alice, report.ReporterId);
        Assert.Equal(Bruno, report.TargetUserId);
        Assert.Equal(ReportCategory.Harassment, report.Category);
        Assert.Equal(new ReportContext.Room(GameSession), report.Context);
        Assert.Equal("Insultos no chat.", report.Narrative.Text);
        Assert.Equal(Start, report.SubmittedAt);
        Assert.Null(report.AssignedModeratorId);
        Assert.Null(report.CurrentActionId);
    }

    [Fact]
    public void EachSubmissionGetsItsOwnProtocol()
    {
        var clock = FixedClock.AtStart();

        Assert.NotEqual(Report.Submit(Submission(), clock).Id, Report.Submit(Submission(), clock).Id);
    }

    [Theory]
    [InlineData(ReportCategory.Harassment, ReportPriority.Normal)]
    [InlineData(ReportCategory.Spam, ReportPriority.Normal)]
    [InlineData(ReportCategory.Other, ReportPriority.Normal)]
    [InlineData(ReportCategory.ThreatOfViolence, ReportPriority.Urgent)]
    [InlineData(ReportCategory.MinorSafety, ReportPriority.Urgent)]
    public void SevereCategoriesArriveWithUrgentPriority(ReportCategory category, ReportPriority expected)
    {
        var report = Report.Submit(Submission(category), FixedClock.AtStart());

        Assert.Equal(expected, report.Priority);
    }

    [Fact]
    public void SelfReportIsRejected()
    {
        var submission = Submission() with { TargetUserId = Alice };

        var violation = Assert.Throws<SafetyRuleViolationException>(
            () => Report.Submit(submission, FixedClock.AtStart()));

        Assert.Equal(SafetyRule.SelfReport, violation.Rule);
    }

    [Fact]
    public void CategoryOutsideTheClosedSetIsRejected()
    {
        var submission = Submission((ReportCategory)999);

        Assert.Throws<ArgumentOutOfRangeException>(() => Report.Submit(submission, FixedClock.AtStart()));
    }

    [Fact]
    public void SubmittingDoesNotBlockButSubmitAndBlockProducesBothEffects()
    {
        var clock = FixedClock.AtStart();
        var onlyReport = Report.Submit(Submission(), clock);

        var both = Report.SubmitAndBlock(Submission(), clock);

        Assert.IsType<Report>(onlyReport);
        Assert.Equal(ReportState.Received, both.Report.State);
        Assert.Equal(new BlockPair(Alice, Bruno), both.Block.Pair);
    }

    [Fact]
    public void ContextIsAnExclusiveArcOfRoomConversationOrProfile()
    {
        var conversation = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
        var photo = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
        ReportContext[] contexts =
        [
            new ReportContext.Room(GameSession),
            new ReportContext.Conversation(conversation),
            new ReportContext.Profile(null),
            new ReportContext.Profile(photo),
        ];

        var stored = contexts.Select(context => Report.Submit(Submission() with { Context = context }, FixedClock.AtStart()).Context);

        Assert.Equal(contexts, stored);
    }

    [Theory]
    [InlineData("room")]
    [InlineData("conversation")]
    [InlineData("profile")]
    public void ContextRejectsEmptyIds(string kind)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => kind switch
        {
            "room" => new ReportContext.Room(Guid.Empty),
            "conversation" => new ReportContext.Conversation(Guid.Empty),
            _ => (ReportContext)new ReportContext.Profile(Guid.Empty),
        });
    }

    [Fact]
    public void SubmitRejectsMissingContext()
    {
        Assert.Throws<ArgumentNullException>(
            () => Report.Submit(Submission() with { Context = null! }, FixedClock.AtStart()));
    }

    [Fact]
    public void TheFullPathWithAppealUpheldEndsActionedOnTheRevision()
    {
        var clock = FixedClock.AtStart();
        var report = Report.Submit(Submission(), clock);

        report.Triage(ReportPriority.High, new ModerationAuthor.Automated());
        Assert.Equal((ReportState.Triaged, ReportPriority.High), (report.State, report.Priority));
        report.StartReview(ModeratorMaria);
        Assert.Equal((ReportState.UnderReview, ModeratorMaria), (report.State, report.AssignedModeratorId));
        var action = report.TakeAction(Decision(), clock);
        Assert.Equal((ReportState.Actioned, action.Id), (report.State, report.CurrentActionId));
        var appeal = Appeal.File(action, Bruno, "Foi um mal-entendido.", clock);
        report.MarkAppealed(appeal);
        Assert.Equal(ReportState.Appealed, report.State);
        appeal.StartReview(ModeratorPedro);
        var revision = appeal.Uphold(ModeratorPedro, "Evidência confirmada.", RevisedPolicy, clock);
        report.ResolveAppeal(revision, clock);

        Assert.Equal((ReportState.Actioned, revision.Id), (report.State, report.CurrentActionId));
        Assert.Null(report.ClosedAt);
    }

    [Fact]
    public void AnAppealOverturnedClosesTheReport()
    {
        var clock = FixedClock.AtStart();
        var (report, action) = ActionedReport(clock);
        var appeal = Appeal.File(action, Bruno, "Foi um mal-entendido.", clock);
        report.MarkAppealed(appeal);
        appeal.StartReview(ModeratorPedro);
        clock.Advance(TimeSpan.FromDays(2));

        var revision = appeal.Overturn(ModeratorPedro, "Evidência insuficiente.", RevisedPolicy, clock);
        report.ResolveAppeal(revision, clock);

        Assert.Equal((ReportState.Closed, revision.Id), (report.State, report.CurrentActionId));
        Assert.Equal(Start.AddDays(2), report.ClosedAt);
    }

    [Fact]
    public void TakeActionLinksTheMeasureToTheReportAndItsTarget()
    {
        var clock = FixedClock.AtStart();
        var report = ReportIn(ReportState.UnderReview, clock);

        var action = report.TakeAction(Decision(), clock);

        Assert.Equal(report.Id, action.ReportId);
        Assert.Equal(Bruno, action.SubjectUserId);
    }

    [Fact]
    public void CloseWithoutActionRecordsReasonAndInstant()
    {
        var clock = FixedClock.AtStart();
        var report = ReportIn(ReportState.UnderReview, clock);
        clock.Advance(TimeSpan.FromHours(3));

        report.Close(ModeratorMaria, "Sem violação da política.", clock);

        Assert.Equal(ReportState.Closed, report.State);
        Assert.Equal("Sem violação da política.", report.ClosingReason);
        Assert.Equal(Start.AddHours(3), report.ClosedAt);
        Assert.Null(report.CurrentActionId);
    }

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public void TransitionFromAnyOtherStateIsRejectedAndLeavesTheReportUnchanged(Transition transition, ReportState state)
    {
        var clock = FixedClock.AtStart();
        var report = ReportIn(state, clock);

        var violation = Assert.Throws<SafetyRuleViolationException>(() => Apply(transition, report, clock));

        Assert.Equal(SafetyRule.TransitionNotAllowed, violation.Rule);
        Assert.Equal(state, report.State);
    }

    [Fact]
    public void AutomatedTriageCannotLowerTheUrgencyOfASevereReport()
    {
        var report = Report.Submit(Submission(ReportCategory.MinorSafety), FixedClock.AtStart());

        var violation = Assert.Throws<SafetyRuleViolationException>(
            () => report.Triage(ReportPriority.Normal, new ModerationAuthor.Automated()));

        Assert.Equal(SafetyRule.AutomatedTriageCannotLowerSevereReport, violation.Rule);
        Assert.Equal((ReportState.Received, ReportPriority.Urgent), (report.State, report.Priority));
    }

    [Fact]
    public void AHumanCanLowerTheUrgencyOfASevereReport()
    {
        var report = Report.Submit(Submission(ReportCategory.MinorSafety), FixedClock.AtStart());

        report.Triage(ReportPriority.Normal, new ModerationAuthor.Human(ModeratorMaria));

        Assert.Equal(ReportPriority.Normal, report.Priority);
    }

    [Theory]
    [InlineData("reporter")]
    [InlineData("target")]
    public void APartyToTheReportCannotReviewIt(string party)
    {
        var report = ReportIn(ReportState.Triaged);

        var violation = Assert.Throws<SafetyRuleViolationException>(
            () => report.StartReview(party == "reporter" ? Alice : Bruno));

        Assert.Equal(SafetyRule.ReviewerMustBeIndependent, violation.Rule);
        Assert.Equal(ReportState.Triaged, report.State);
    }

    [Theory]
    [InlineData("other-moderator")]
    [InlineData("automated")]
    public void OnlyTheAssignedModeratorCanTakeAction(string author)
    {
        var report = ReportIn(ReportState.UnderReview);
        ModerationAuthor decisionAuthor = author == "automated"
            ? new ModerationAuthor.Automated()
            : new ModerationAuthor.Human(ModeratorPedro);

        var violation = Assert.Throws<SafetyRuleViolationException>(
            () => report.TakeAction(Decision(ModerationMeasure.Warning, decisionAuthor), FixedClock.AtStart()));

        Assert.Equal(SafetyRule.OnlyAssignedReviewerCanDecide, violation.Rule);
        Assert.Equal(ReportState.UnderReview, report.State);
    }

    [Fact]
    public void OnlyTheAssignedModeratorCanClose()
    {
        var report = ReportIn(ReportState.UnderReview);

        var violation = Assert.Throws<SafetyRuleViolationException>(
            () => report.Close(ModeratorPedro, "Sem violação.", FixedClock.AtStart()));

        Assert.Equal(SafetyRule.OnlyAssignedReviewerCanDecide, violation.Rule);
    }

    [Fact]
    public void MarkAppealedRejectsAnAppealOfAnotherMeasure()
    {
        var clock = FixedClock.AtStart();
        var report = ReportIn(ReportState.Actioned, clock);
        var unrelated = Appeal.File(SanctionOn(Bruno), Bruno, "Outro caso.", clock);

        Assert.Throws<ArgumentException>(() => report.MarkAppealed(unrelated));
        Assert.Equal(ReportState.Actioned, report.State);
    }

    [Fact]
    public void ResolveAppealRejectsARevisionOfAnotherMeasure()
    {
        var clock = FixedClock.AtStart();
        var report = ReportIn(ReportState.Appealed, clock);
        var unrelated = Appeal.File(SanctionOn(Bruno), Bruno, "Outro caso.", clock);
        unrelated.StartReview(ModeratorPedro);
        var foreignRevision = unrelated.Overturn(ModeratorPedro, "Outro caso.", RevisedPolicy, clock);

        Assert.Throws<ArgumentException>(() => report.ResolveAppeal(foreignRevision, clock));
        Assert.Equal(ReportState.Appealed, report.State);
    }

    [Fact]
    public void ReporterSeesOnlyTheAllowedStatusFields()
    {
        var report = Report.Submit(Submission(), FixedClock.AtStart());

        var view = report.GetReportStatus(Alice);

        Assert.NotNull(view);
        var keys = typeof(ReportStatusView).GetProperties().Select(property => property.Name).Order();
        Assert.Equal(["Category", "Protocol", "Status", "SubmittedAt"], keys);
        Assert.Equal(
            new ReportStatusView(report.Id, ReportCategory.Harassment, ReportStatusForReporter.Received, Start),
            view);
    }

    [Theory]
    [InlineData(ReportState.Received, ReportStatusForReporter.Received)]
    [InlineData(ReportState.Triaged, ReportStatusForReporter.Received)]
    [InlineData(ReportState.UnderReview, ReportStatusForReporter.UnderReview)]
    [InlineData(ReportState.Actioned, ReportStatusForReporter.Concluded)]
    [InlineData(ReportState.Closed, ReportStatusForReporter.Concluded)]
    [InlineData(ReportState.Appealed, ReportStatusForReporter.Concluded)]
    public void ReporterSeesACoarseStatusThatHidesTheOutcome(ReportState state, ReportStatusForReporter expected)
    {
        var view = ReportIn(state).GetReportStatus(Alice);

        Assert.Equal(expected, view?.Status);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("moderator")]
    [InlineData("stranger")]
    public void NobodyButTheReporterSeesTheReportStatus(string requester)
    {
        var report = ReportIn(ReportState.UnderReview);
        var requesterId = requester switch
        {
            "target" => Bruno,
            "moderator" => ModeratorMaria,
            _ => Carla,
        };

        Assert.Null(report.GetReportStatus(requesterId));
    }

    private static void Apply(Transition transition, Report report, TimeProvider clock)
    {
        switch (transition)
        {
            case Transition.Triage:
                report.Triage(ReportPriority.Normal, new ModerationAuthor.Automated());
                break;
            case Transition.StartReview:
                report.StartReview(ModeratorMaria);
                break;
            case Transition.TakeAction:
                report.TakeAction(Decision(), clock);
                break;
            case Transition.Close:
                report.Close(ModeratorMaria, "Sem violação.", clock);
                break;
            case Transition.MarkAppealed:
                report.MarkAppealed(Appeal.File(SanctionOn(Bruno), Bruno, "Recurso.", clock));
                break;
            case Transition.ResolveAppeal:
                var appeal = Appeal.File(SanctionOn(Bruno), Bruno, "Recurso.", clock);
                appeal.StartReview(ModeratorPedro);
                report.ResolveAppeal(appeal.Overturn(ModeratorPedro, "Revisto.", RevisedPolicy, clock), clock);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition));
        }
    }

    private static (Report Report, ModerationAction Action) ActionedReport(TimeProvider clock)
    {
        var report = ReportIn(ReportState.UnderReview, clock);
        return (report, report.TakeAction(Decision(), clock));
    }
}
