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
        var report = Report.Submit(Submission(), FixedClock.AtStart()).Accepted();

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

        Assert.NotEqual(
            Report.Submit(Submission(), clock).Accepted().Id,
            Report.Submit(Submission(), clock).Accepted().Id);
    }

    [Theory]
    [InlineData(ReportCategory.Harassment, ReportPriority.Normal)]
    [InlineData(ReportCategory.Spam, ReportPriority.Normal)]
    [InlineData(ReportCategory.Other, ReportPriority.Normal)]
    [InlineData(ReportCategory.ThreatOfViolence, ReportPriority.Urgent)]
    [InlineData(ReportCategory.MinorSafety, ReportPriority.Urgent)]
    public void SevereCategoriesArriveWithUrgentPriority(ReportCategory category, ReportPriority expected)
    {
        var report = Report.Submit(Submission(category), FixedClock.AtStart()).Accepted();

        Assert.Equal(expected, report.Priority);
    }

    [Fact]
    public void SelfReportIsRejected()
    {
        var submission = Submission() with { TargetUserId = Alice };

        SafetyAssert.Rejected(SafetyRejection.SelfReport, Report.Submit(submission, FixedClock.AtStart()));
    }

    [Fact]
    public void SelfReportAndBlockIsRejectedWithoutProducingABlock()
    {
        var submission = Submission() with { TargetUserId = Alice };

        SafetyAssert.Rejected(SafetyRejection.SelfReport, Report.SubmitAndBlock(submission, FixedClock.AtStart()));
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
        var onlyReport = Report.Submit(Submission(), clock).Accepted();

        var both = Report.SubmitAndBlock(Submission(), clock).Accepted();

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

        var stored = contexts.Select(
            context => Report.Submit(Submission() with { Context = context }, FixedClock.AtStart()).Accepted().Context);

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
        var report = Report.Submit(Submission(), clock).Accepted();

        report.Triage(ReportPriority.High, new ModerationAuthor.Automated()).Accepted();
        Assert.Equal((ReportState.Triaged, ReportPriority.High), (report.State, report.Priority));
        report.StartReview(ModeratorMaria).Accepted();
        Assert.Equal((ReportState.UnderReview, ModeratorMaria), (report.State, report.AssignedModeratorId));
        var action = report.TakeAction(Decision(), Limits, clock).Accepted();
        Assert.Equal((ReportState.Actioned, action.Id), (report.State, report.CurrentActionId));
        var appeal = Appeal.File(action, Bruno, "Foi um mal-entendido.", clock).Accepted();
        report.MarkAppealed(appeal).Accepted();
        Assert.Equal(ReportState.Appealed, report.State);
        appeal.StartReview(ModeratorPedro).Accepted();
        var revision = appeal.Uphold(ModeratorPedro, Ruling("Evidência confirmada."), clock).Accepted();
        report.ResolveAppeal(revision, clock).Accepted();

        Assert.Equal((ReportState.Actioned, revision.Id), (report.State, report.CurrentActionId));
        Assert.Null(report.ClosedAt);
    }

    [Fact]
    public void AnAppealOverturnedClosesTheReport()
    {
        var clock = FixedClock.AtStart();
        var (report, action) = ActionedReport(clock);
        var appeal = Appeal.File(action, Bruno, "Foi um mal-entendido.", clock).Accepted();
        report.MarkAppealed(appeal).Accepted();
        appeal.StartReview(ModeratorPedro).Accepted();
        clock.Advance(TimeSpan.FromDays(2));

        var revision = appeal.Overturn(ModeratorPedro, Ruling("Evidência insuficiente."), clock).Accepted();
        report.ResolveAppeal(revision, clock).Accepted();

        Assert.Equal((ReportState.Closed, revision.Id), (report.State, report.CurrentActionId));
        Assert.Equal(Start.AddDays(2), report.ClosedAt);
    }

    [Fact]
    public void TakeActionLinksTheMeasureToTheReportAndItsTarget()
    {
        var (report, action) = ActionedReport(FixedClock.AtStart());

        Assert.Equal(report.Id, action.ReportId);
        Assert.Equal(Bruno, action.SubjectUserId);
    }

    [Fact]
    public void CloseWithoutActionRecordsReasonAndInstant()
    {
        var clock = FixedClock.AtStart();
        var report = ReportIn(ReportState.UnderReview, clock);
        clock.Advance(TimeSpan.FromHours(3));

        report.Close(ModeratorMaria, "Sem violação da política.", clock).Accepted();

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

        var rejection = Apply(transition, report, clock);

        Assert.Equal(SafetyRejection.TransitionNotAllowed, rejection);
        Assert.Equal(state, report.State);
    }

    [Fact]
    public void ARepeatedTransitionIsRejectedLikeADoubleClick()
    {
        var report = ReportIn(ReportState.Received);
        report.Triage(ReportPriority.Normal, new ModerationAuthor.Automated()).Accepted();

        SafetyAssert.Rejected(
            SafetyRejection.TransitionNotAllowed,
            report.Triage(ReportPriority.High, new ModerationAuthor.Automated()));
        Assert.Equal(ReportPriority.Normal, report.Priority);
    }

    [Fact]
    public void AutomatedTriageCannotLowerTheUrgencyOfASevereReport()
    {
        var report = Report.Submit(Submission(ReportCategory.MinorSafety), FixedClock.AtStart()).Accepted();

        var result = report.Triage(ReportPriority.Normal, new ModerationAuthor.Automated());

        SafetyAssert.Rejected(SafetyRejection.AutomatedTriageCannotLowerSevereReport, result);
        Assert.Equal((ReportState.Received, ReportPriority.Urgent), (report.State, report.Priority));
    }

    [Fact]
    public void AHumanCanLowerTheUrgencyOfASevereReport()
    {
        var report = Report.Submit(Submission(ReportCategory.MinorSafety), FixedClock.AtStart()).Accepted();

        report.Triage(ReportPriority.Normal, new ModerationAuthor.Human(ModeratorMaria)).Accepted();

        Assert.Equal(ReportPriority.Normal, report.Priority);
    }

    [Theory]
    [InlineData("reporter")]
    [InlineData("target")]
    public void APartyToTheReportCannotReviewIt(string party)
    {
        var report = ReportIn(ReportState.Triaged);

        var result = report.StartReview(party == "reporter" ? Alice : Bruno);

        SafetyAssert.Rejected(SafetyRejection.ReviewerMustBeIndependent, result);
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

        var result = report.TakeAction(Decision(ModerationMeasure.Restriction, decisionAuthor), Limits, FixedClock.AtStart());

        SafetyAssert.Rejected(SafetyRejection.OnlyAssignedReviewerCanDecide, result);
        Assert.Equal(ReportState.UnderReview, report.State);
    }

    [Fact]
    public void OnlyTheAssignedModeratorCanClose()
    {
        var report = ReportIn(ReportState.UnderReview);

        var result = report.Close(ModeratorPedro, "Sem violação.", FixedClock.AtStart());

        SafetyAssert.Rejected(SafetyRejection.OnlyAssignedReviewerCanDecide, result);
    }

    [Fact]
    public void MarkAppealedRejectsAnAppealOfAnotherMeasure()
    {
        var clock = FixedClock.AtStart();
        var report = ReportIn(ReportState.Actioned, clock);
        var unrelated = Appeal.File(SanctionOn(Bruno), Bruno, "Outro caso.", clock).Accepted();

        Assert.Throws<ArgumentException>(() => report.MarkAppealed(unrelated));
        Assert.Equal(ReportState.Actioned, report.State);
    }

    [Fact]
    public void ResolveAppealRejectsARevisionOfAnotherMeasure()
    {
        var clock = FixedClock.AtStart();
        var report = ReportIn(ReportState.Appealed, clock);
        var foreignRevision = AppealUnderReview(SanctionOn(Bruno), clock)
            .Overturn(ModeratorPedro, Ruling("Outro caso."), clock)
            .Accepted();

        Assert.Throws<ArgumentException>(() => report.ResolveAppeal(foreignRevision, clock));
        Assert.Equal(ReportState.Appealed, report.State);
    }

    [Fact]
    public void ReporterSeesOnlyTheAllowedStatusFields()
    {
        var report = Report.Submit(Submission(), FixedClock.AtStart()).Accepted();

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

    private static SafetyRejection? Apply(Transition transition, Report report, TimeProvider clock) => transition switch
    {
        Transition.Triage => report.Triage(ReportPriority.Normal, new ModerationAuthor.Automated()).Rejection,
        Transition.StartReview => report.StartReview(ModeratorMaria).Rejection,
        Transition.TakeAction => report.TakeAction(Decision(), Limits, clock).Rejection,
        Transition.Close => report.Close(ModeratorMaria, "Sem violação.", clock).Rejection,
        Transition.MarkAppealed => report.MarkAppealed(
            Appeal.File(SanctionOn(Bruno), Bruno, "Recurso.", clock).Accepted()).Rejection,
        Transition.ResolveAppeal => report.ResolveAppeal(
            AppealUnderReview(SanctionOn(Bruno), clock).Overturn(ModeratorPedro, Ruling("Revisto."), clock).Accepted(),
            clock).Rejection,
        _ => throw new ArgumentOutOfRangeException(nameof(transition)),
    };

    private static (Report Report, ModerationAction Action) ActionedReport(TimeProvider clock)
    {
        var report = ReportIn(ReportState.UnderReview, clock);
        return (report, report.TakeAction(Decision(), Limits, clock).Accepted());
    }
}
