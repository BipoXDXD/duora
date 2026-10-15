using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;
using Validation = Duora.Core.Experiences.ActionValidation<
    Duora.Core.Experiences.UnlikelyRoutes.UnlikelyRoutesState,
    Duora.Core.Experiences.UnlikelyRoutes.UnlikelyRoutesAction>;

namespace Duora.UnitTests.Experiences;

/// <summary>Aceite da spec 6.2 e transições de etapa de Rotas Improváveis.</summary>
public sealed class UnlikelyRoutesFlowTests
{
    private readonly ManualTimeProvider _clock = ClockAtRoundStart();
    private readonly UnlikelyRoutesEngine _engine;

    public UnlikelyRoutesFlowTests() => _engine = Engine(_clock);

    [Fact]
    public void TwoParticipantsCompleteTheSessionWithASharedRoute()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ConfirmRoute());
        session = Act(_engine, session, Bia, new UnlikelyRoutesAction.ConfirmRoute());

        var view = _engine.ProjectForParticipant(session, Bia)!;
        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.Equal(RoutesStage.Conclusion, view.Stage);
        Assert.Equal(
            ["Farol desativado", "Estação de bondinhos", "Feira de mapas antigos", "Observatório de nuvens"],
            view.Route.Select(stop => stop.OptionLabel));
        Assert.Empty(view.AvailableActions);
    }

    [Fact]
    public void NewSessionStartsAtIntroductionWithVersionOneAndNoClues()
    {
        var session = NewSession(_engine);

        var view = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(new StateVersion(1), session.Version);
        Assert.Equal(RoutesStage.Introduction, view.Stage);
        Assert.Equal("Montar juntos um passeio por uma cidade inventada.", view.Objective);
        Assert.Empty(view.MyClues);
        Assert.Equal(["acknowledge"], view.AvailableActions);
    }

    [Fact]
    public void EachAppliedActionIncrementsTheStateVersionByOne()
    {
        var session = NewSession(_engine);

        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        Assert.Equal(new StateVersion(2), session.Version);

        session = Act(_engine, session, Bia, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        Assert.Equal(new StateVersion(3), session.Version);
    }

    [Fact]
    public void IntroductionWaitsForBothAcknowledgementsBeforeShowingClues()
    {
        var session = NewSession(_engine);

        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        var afterOne = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(RoutesStage.Introduction, afterOne.Stage);
        Assert.True(afterOne.AwaitingPartner);

        session = Act(_engine, session, Bia, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        var afterBoth = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(RoutesStage.Clues, afterBoth.Stage);
        Assert.Equal([AnaClueOne, AnaClueTwo], afterBoth.MyClues);
        Assert.False(afterBoth.AwaitingPartner);
    }

    [Fact]
    public void CluesStageAdvancesToChoicesWithTheFirstStopOffered()
    {
        var session = AtChoices(_engine, NewSession(_engine));

        var view = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(RoutesStage.Choices, view.Stage);
        Assert.True(view.IsMyTurn);
        Assert.Equal("route-stop-1", view.CurrentStop!.StepId);
        Assert.Equal(["lighthouse", "floating-market", "underground-library"],
            view.CurrentStop.Options.Select(option => option.OptionId));
        Assert.Equal(["choose-option"], view.AvailableActions);
    }

    [Fact]
    public void ChoicesAlternateBetweenParticipants()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));

        var anaView = _engine.ProjectForParticipant(session, Ana)!;
        var biaView = _engine.ProjectForParticipant(session, Bia)!;
        Assert.False(anaView.IsMyTurn);
        Assert.True(anaView.AwaitingPartner);
        Assert.Empty(anaView.AvailableActions);
        Assert.True(biaView.IsMyTurn);
        Assert.Equal("route-stop-2", biaView.CurrentStop!.StepId);
    }

    [Fact]
    public void LastChoiceMovesTheSessionToCooperativeAdjustment()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));

        var view = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(RoutesStage.Adjustment, view.Stage);
        Assert.Null(view.CurrentStop);
        Assert.Equal(4, view.Route.Count);
        Assert.Equal(1, view.RevisionsRemaining);
        Assert.Equal(["confirm-route", "request-revision"], view.AvailableActions);
    }

    [Fact]
    public void OneConfirmationKeepsTheSessionInAdjustment()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));

        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ConfirmRoute());

        var view = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(RoutesStage.Adjustment, view.Stage);
        Assert.True(view.AwaitingPartner);
        Assert.Equal(["request-revision"], view.AvailableActions);
    }

    [Fact]
    public void RevisionClearsTheRouteAndReopensChoices()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ConfirmRoute());

        session = Act(_engine, session, Bia, new UnlikelyRoutesAction.RequestRevision());

        var view = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(RoutesStage.Choices, view.Stage);
        Assert.Empty(view.Route);
        Assert.Equal(0, view.RevisionsRemaining);
        Assert.Equal("route-stop-1", view.CurrentStop!.StepId);
        Assert.True(view.IsMyTurn);
    }

    [Fact]
    public void RevisionLimitIsEnforcedByTheServer()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));
        session = Act(_engine, session, Bia, new UnlikelyRoutesAction.RequestRevision());
        session = RedoChoices(session);

        var second = _engine.ValidateAction(session, Command(session, Ana, new UnlikelyRoutesAction.RequestRevision()));

        AssertRejected(RejectionReason.RevisionLimitReached, second);
        Assert.Equal(["confirm-route"], _engine.ProjectForParticipant(session, Ana)!.AvailableActions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ConfiguredRevisionLimitAllowsExactlyThatManyRevisions(int maxRevisions)
    {
        var engine = Engine(_clock, maxRevisions);
        var session = AtAdjustment(engine, NewSession(engine));

        for (var used = 0; used < maxRevisions; used++)
        {
            session = Act(engine, session, Ana, new UnlikelyRoutesAction.RequestRevision());
            session = RedoChoices(session, engine);
        }

        var extra = engine.ValidateAction(session, Command(session, Bia, new UnlikelyRoutesAction.RequestRevision()));
        AssertRejected(RejectionReason.RevisionLimitReached, extra);
    }

    [Fact]
    public void ResendingTheSameActionIdDoesNotConsumeTwoChoices()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        var choose = Command(session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));
        var afterFirst = Apply(_engine, session, choose);

        var resend = _engine.ValidateAction(afterFirst, choose);

        var replayed = Assert.IsType<Validation.AlreadyApplied>(resend);
        Assert.Equal(new ActionReceipt(choose.ActionId, new StateVersion(6)), replayed.Receipt);
        Assert.Equal(new StateVersion(6), afterFirst.Version);
        var view = _engine.ProjectForParticipant(afterFirst, Ana)!;
        Assert.Single(view.Route);
        Assert.Equal("route-stop-2", view.CurrentStop!.StepId);
    }

    [Fact]
    public void ResendIsRecognisedEvenAfterLaterActions()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        var choose = Command(session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));
        session = Apply(_engine, session, choose);
        var versionAfterChoice = session.Version;
        session = Act(_engine, session, Bia, new UnlikelyRoutesAction.ChooseOption("route-stop-2", "clock-garden"));

        var resend = _engine.ValidateAction(session, choose);

        var replayed = Assert.IsType<Validation.AlreadyApplied>(resend);
        Assert.Equal(versionAfterChoice, replayed.Receipt.ResourceVersion);
    }

    [Fact]
    public void ReusingAnActionIdWithADifferentActionIsRejected()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        var choose = Command(session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));
        session = Apply(_engine, session, choose);

        var reused = choose with
        {
            ExpectedVersion = session.Version,
            Action = new UnlikelyRoutesAction.ChooseOption("route-stop-2", "clock-garden"),
        };

        AssertRejected(RejectionReason.ActionIdReused, _engine.ValidateAction(session, reused));
    }

    [Fact]
    public void ReusingAnActionIdWithTheSameActionButAnotherExpectedVersionIsRejected()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        var choose = Command(session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));
        session = Apply(_engine, session, choose);

        var otherBody = choose with { ExpectedVersion = session.Version };

        AssertRejected(RejectionReason.ActionIdReused, _engine.ValidateAction(session, otherBody));
    }

    [Fact]
    public void ReusingAnActionIdFromAnotherParticipantIsRejected()
    {
        var session = NewSession(_engine);
        var acknowledge = Command(session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        session = Apply(_engine, session, acknowledge);

        var stolen = acknowledge with { Actor = Bia, ExpectedVersion = session.Version };

        AssertRejected(RejectionReason.ActionIdReused, _engine.ValidateAction(session, stolen));
    }

    [Fact]
    public void DivergentExpectedVersionIsATypedConflict()
    {
        var session = NewSession(_engine);
        var stale = Command(session, Bia, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));

        var validation = _engine.ValidateAction(session, stale);

        var conflict = Assert.IsType<Validation.Conflict>(validation);
        Assert.Equal(new StateVersion(2), conflict.CurrentVersion);
    }

    [Fact]
    public void ExpectedVersionAheadOfTheServerIsAlsoAConflict()
    {
        var session = NewSession(_engine);
        var command = Command(session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction)) with
        {
            ExpectedVersion = new StateVersion(2),
        };

        Assert.IsType<Validation.Conflict>(_engine.ValidateAction(session, command));
    }

    [Fact]
    public void TwoActionsFromTheSameSnapshotOnlyOneApplies()
    {
        var snapshot = NewSession(_engine);
        var fromAna = Command(snapshot, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        var fromBia = Command(snapshot, Bia, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));

        var afterAna = Apply(_engine, snapshot, fromAna);

        Assert.IsType<Validation.Conflict>(_engine.ValidateAction(afterAna, fromBia));
    }

    [Fact]
    public void ApplyingTheSameValidatedActionTwiceGivesTheSameResult()
    {
        var session = NewSession(_engine);
        var accepted = Assert.IsType<Validation.Accepted>(_engine.ValidateAction(
            session, Command(session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction))));

        var first = _engine.ApplyAction(accepted.Action);
        var second = _engine.ApplyAction(accepted.Action);

        // A ação validada carrega o estado em que foi validada: aplicar é uma função pura dele. Qual dos dois
        // resultados é gravado decide a atualização condicionada pela versão, na persistência.
        Assert.Equal(first.Session.Version, second.Session.Version);
        Assert.Equal(first.Receipt, second.Receipt);
        Assert.Equal(new StateVersion(1), session.Version);
    }

    [Fact]
    public void AppliedActionReportsReceiptAndEvent()
    {
        var session = NewSession(_engine);
        var command = Command(session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        var accepted = Assert.IsType<Validation.Accepted>(_engine.ValidateAction(session, command));

        var applied = _engine.ApplyAction(accepted.Action);

        Assert.Equal(new ActionReceipt(command.ActionId, new StateVersion(2)), applied.Receipt);
        var applyEvent = Assert.IsType<ActionApplied>(Assert.Single(applied.Events));
        Assert.Equal(
            new ActionApplied(command.ActionId, Ana, new ActionType("acknowledge"), new StateVersion(2), RoundStart),
            applyEvent);
    }

    [Fact]
    public void FinalConfirmationEmitsSessionFinishedAsCompleted()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ConfirmRoute());
        var accepted = Assert.IsType<Validation.Accepted>(
            _engine.ValidateAction(session, Command(session, Bia, new UnlikelyRoutesAction.ConfirmRoute())));

        var applied = _engine.ApplyAction(accepted.Action);

        var finished = Assert.IsType<SessionFinished>(applied.Events[^1]);
        Assert.Equal(SessionStatus.Completed, finished.Outcome);
        Assert.Equal(applied.Session.Version, finished.Version);
    }

    [Fact]
    public void ActionFromSomeoneOutsideThePairIsRejected()
    {
        var session = NewSession(_engine);

        var validation = _engine.ValidateAction(
            session, Command(session, Outsider, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction)));

        AssertRejected(RejectionReason.NotParticipant, validation);
    }

    [Fact]
    public void OutsiderReusingAKnownActionIdGetsNoReceipt()
    {
        var session = NewSession(_engine);
        var acknowledge = Command(session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        session = Apply(_engine, session, acknowledge);

        AssertRejected(RejectionReason.NotParticipant, _engine.ValidateAction(session, acknowledge with { Actor = Outsider }));
    }

    [Fact]
    public void ActionAfterCompletionIsRejected()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ConfirmRoute());
        session = Act(_engine, session, Bia, new UnlikelyRoutesAction.ConfirmRoute());

        var late = _engine.ValidateAction(session, Command(session, Ana, new UnlikelyRoutesAction.RequestRevision()));

        AssertRejected(RejectionReason.SessionFinished, late);
    }

    public static TheoryData<UnlikelyRoutesAction> ActionsOutOfStageAtIntroduction => new()
    {
        new UnlikelyRoutesAction.Acknowledge(RoutesStage.Clues),
        new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"),
        new UnlikelyRoutesAction.ConfirmRoute(),
        new UnlikelyRoutesAction.RequestRevision(),
    };

    [Theory]
    [MemberData(nameof(ActionsOutOfStageAtIntroduction))]
    public void ActionsOutsideTheIntroductionAreRejectedAtIntroduction(UnlikelyRoutesAction action)
    {
        var session = NewSession(_engine);

        AssertRejected(RejectionReason.WrongStage, _engine.ValidateAction(session, Command(session, Ana, action)));
    }

    public static TheoryData<UnlikelyRoutesAction> ActionsOutOfStageAtChoices => new()
    {
        new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction),
        new UnlikelyRoutesAction.Acknowledge(RoutesStage.Clues),
        new UnlikelyRoutesAction.Acknowledge(RoutesStage.Choices),
        new UnlikelyRoutesAction.ConfirmRoute(),
        new UnlikelyRoutesAction.RequestRevision(),
    };

    [Theory]
    [MemberData(nameof(ActionsOutOfStageAtChoices))]
    public void ActionsOutsideChoicesAreRejectedDuringChoices(UnlikelyRoutesAction action)
    {
        var session = AtChoices(_engine, NewSession(_engine));

        AssertRejected(RejectionReason.WrongStage, _engine.ValidateAction(session, Command(session, Ana, action)));
    }

    public static TheoryData<UnlikelyRoutesAction> ActionsOutOfStageAtAdjustment => new()
    {
        new UnlikelyRoutesAction.Acknowledge(RoutesStage.Adjustment),
        new UnlikelyRoutesAction.ChooseOption("route-stop-4", "quiet-tower"),
    };

    [Theory]
    [MemberData(nameof(ActionsOutOfStageAtAdjustment))]
    public void ActionsOutsideAdjustmentAreRejectedDuringAdjustment(UnlikelyRoutesAction action)
    {
        var session = AtAdjustment(_engine, NewSession(_engine));

        AssertRejected(RejectionReason.WrongStage, _engine.ValidateAction(session, Command(session, Ana, action)));
    }

    [Fact]
    public void AcknowledgingTwiceWithANewActionIdIsRejected()
    {
        var session = NewSession(_engine);
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));

        var again = _engine.ValidateAction(
            session, Command(session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction)));

        AssertRejected(RejectionReason.AlreadyActed, again);
    }

    [Fact]
    public void ConfirmingTwiceWithANewActionIdIsRejected()
    {
        var session = AtAdjustment(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ConfirmRoute());

        var again = _engine.ValidateAction(session, Command(session, Ana, new UnlikelyRoutesAction.ConfirmRoute()));

        AssertRejected(RejectionReason.AlreadyActed, again);
    }

    [Fact]
    public void ChoosingOutOfTurnIsRejected()
    {
        var session = AtChoices(_engine, NewSession(_engine));

        var validation = _engine.ValidateAction(
            session, Command(session, Bia, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse")));

        AssertRejected(RejectionReason.NotYourTurn, validation);
    }

    [Theory]
    [InlineData("route-stop-2")]
    [InlineData("route-stop-0")]
    [InlineData("")]
    public void ChoosingForAnotherStepIsRejected(string stepId)
    {
        var session = AtChoices(_engine, NewSession(_engine));

        var validation = _engine.ValidateAction(
            session, Command(session, Ana, new UnlikelyRoutesAction.ChooseOption(stepId, "lighthouse")));

        AssertRejected(RejectionReason.StaleStep, validation);
    }

    [Theory]
    [InlineData("clock-garden")]
    [InlineData("museum")]
    [InlineData("")]
    public void ChoosingAnOptionNotApprovedForTheStepIsRejected(string optionId)
    {
        var session = AtChoices(_engine, NewSession(_engine));

        var validation = _engine.ValidateAction(
            session, Command(session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", optionId)));

        AssertRejected(RejectionReason.UnknownOption, validation);
    }

    [Fact]
    public void RejectedActionDoesNotChangeTheSession()
    {
        var session = AtChoices(_engine, NewSession(_engine));

        _engine.ValidateAction(
            session, Command(session, Bia, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse")));

        Assert.Equal(new StateVersion(5), session.Version);
        Assert.Empty(_engine.ProjectForParticipant(session, Ana)!.Route);
    }

    [Fact]
    public void SameSeedProducesTheSameAssignment()
    {
        var first = _engine.ProjectForParticipant(AtChoices(_engine, NewSession(_engine, seed: new SessionSeed(42))), Ana)!;
        var second = _engine.ProjectForParticipant(AtChoices(_engine, NewSession(_engine, seed: new SessionSeed(42))), Ana)!;

        Assert.Equal(first.MyClues, second.MyClues);
        Assert.Equal(first.IsMyTurn, second.IsMyTurn);
    }

    [Fact]
    public void SeedDecidesClueSetsAndFirstChooser()
    {
        var views = Enumerable.Range(0, 32)
            .Select(seed => _engine.ProjectForParticipant(
                AtChoices(_engine, NewSession(_engine, seed: new SessionSeed(seed))), Ana)!)
            .ToList();

        Assert.Contains(views, view => view.MyClues.Contains(AnaClueOne) && view.IsMyTurn);
        Assert.Contains(views, view => view.MyClues.Contains(BiaClueOne) && view.IsMyTurn);
        Assert.Contains(views, view => view.MyClues.Contains(AnaClueOne) && !view.IsMyTurn);
        Assert.Contains(views, view => view.MyClues.Contains(BiaClueOne) && !view.IsMyTurn);
    }

    [Fact]
    public void FixtureSeedGivesAnaTheFirstClueSetAndTheFirstTurn()
    {
        var view = _engine.ProjectForParticipant(AtChoices(_engine, NewSession(_engine)), Ana)!;

        Assert.Equal([AnaClueOne, AnaClueTwo], view.MyClues);
        Assert.True(view.IsMyTurn);
    }

    private UnlikelyRoutesSession RedoChoices(UnlikelyRoutesSession session, UnlikelyRoutesEngine? engine = null)
    {
        engine ??= _engine;
        session = Act(engine, session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "floating-market"));
        session = Act(engine, session, Bia, new UnlikelyRoutesAction.ChooseOption("route-stop-2", "clock-garden"));
        session = Act(engine, session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-3", "violin-bridge"));
        return Act(engine, session, Bia, new UnlikelyRoutesAction.ChooseOption("route-stop-4", "quiet-tower"));
    }

    private static void AssertRejected(RejectionReason expected, Validation validation)
    {
        var rejected = Assert.IsType<Validation.Rejected>(validation);
        Assert.Equal(expected, rejected.Reason);
    }
}
