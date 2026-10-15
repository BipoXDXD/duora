using Duora.Core.Experiences;
using Duora.Core.Experiences.Constellation;
using static Duora.UnitTests.Experiences.Constellation.ConstellationFixture;
using Validation = Duora.Core.Experiences.ActionValidation<
    Duora.Core.Experiences.Constellation.ConstellationState,
    Duora.Core.Experiences.Constellation.ConstellationAction>;

namespace Duora.UnitTests.Experiences.Constellation;

/// <summary>Aceite da spec 6.3: cartão, escolha privada, revelação por dupla confirmação e encerramento.</summary>
public sealed class ConstellationFlowTests
{
    private readonly ConstellationEngine _engine = Engine(ClockAtRoundStart());

    [Fact]
    public void TwoParticipantsAnswerEveryCardAndTheSummaryShowsOnlyRevealedCards()
    {
        var session = AtThirdCard(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, Choose(IceCreamCard, "corn-cake"));
        session = Act(_engine, session, Bia, Choose(IceCreamCard, "cinnamon-coffee"));

        var view = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.Equal(ConstellationStage.Summary, view.Stage);
        Assert.Equal(
            [
                new RevealedCardView(SundayCard, "Para um domingo sem planos, qual destas?", StreetFair, StreetFair, true),
                new RevealedCardView(
                    IceCreamCard, "Que sabor de sorvete inventado você provaria primeiro?", CornCake, CinnamonCoffee, false),
            ],
            view.Revealed);
        Assert.Equal([new PassedCardView(PowerCard, "Que superpoder inútil você escolheria?")], view.Passed);
        Assert.Null(view.CurrentCard);
        Assert.Null(view.MyAnswer);
        Assert.False(view.AwaitingPartner);
        Assert.Empty(view.AvailableActions);
    }

    [Fact]
    public void NewSessionOpensTheFirstCardWithChooseAndSkip()
    {
        var session = NewSession(_engine);

        var view = _engine.ProjectForParticipant(session, Ana)!;
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(new StateVersion(1), session.Version);
        Assert.Equal(ConstellationStage.Cards, view.Stage);
        Assert.Equal(3, view.CardCount);
        Assert.Equal((SundayCard, 1), (view.CurrentCard!.CardId, view.CurrentCard.Position));
        Assert.Equal(["choose-option", "skip-card"], view.AvailableActions);
        Assert.Null(view.MyAnswer);
        Assert.False(view.AwaitingPartner);
        Assert.Empty(view.Revealed);
        Assert.Empty(view.Passed);
    }

    [Fact]
    public void BothParticipantsReceiveTheSameCardWithTheSameApprovedOptions()
    {
        var session = NewSession(_engine);

        var anaCard = _engine.ProjectForParticipant(session, Ana)!.CurrentCard!;
        var biaCard = _engine.ProjectForParticipant(session, Bia)!.CurrentCard!;

        Assert.Equal(
            [
                new CardOptionView("picnic", Picnic),
                new CardOptionView("movies", Movies),
                new CardOptionView("street-fair", StreetFair),
                new CardOptionView("new-recipe", NewRecipe),
            ],
            anaCard.Options);
        Assert.Equal(anaCard.Options, biaCard.Options);
        Assert.Equal((anaCard.CardId, anaCard.Prompt), (biaCard.CardId, biaCard.Prompt));
    }

    [Fact]
    public void OneAnswerKeepsTheCardOpenUntilThePartnerAnswers()
    {
        var session = Act(_engine, NewSession(_engine), Ana, Choose(SundayCard, "movies"));

        var anaView = _engine.ProjectForParticipant(session, Ana)!;
        var biaView = _engine.ProjectForParticipant(session, Bia)!;
        Assert.Equal(new OwnAnswerView(OwnAnswerKind.Chosen, new CardOptionView("movies", Movies)), anaView.MyAnswer);
        Assert.True(anaView.AwaitingPartner);
        Assert.Empty(anaView.AvailableActions);
        Assert.Equal(SundayCard, anaView.CurrentCard!.CardId);
        Assert.Null(biaView.MyAnswer);
        Assert.False(biaView.AwaitingPartner);
        Assert.Equal(["choose-option", "skip-card"], biaView.AvailableActions);
        Assert.Empty(biaView.Revealed);
    }

    [Fact]
    public void OwnSkipIsShownOnlyAsSkippedWithoutAnOption()
    {
        var session = Act(_engine, NewSession(_engine), Bia, Skip(SundayCard));

        var biaView = _engine.ProjectForParticipant(session, Bia)!;

        Assert.Equal(new OwnAnswerView(OwnAnswerKind.Skipped, Option: null), biaView.MyAnswer);
        Assert.True(biaView.AwaitingPartner);
    }

    [Fact]
    public void DoubleConfirmationRevealsTheCardAndOpensTheNextOne()
    {
        var session = NewSession(_engine);
        session = Act(_engine, session, Ana, Choose(SundayCard, "picnic"));
        session = Act(_engine, session, Bia, Choose(SundayCard, "new-recipe"));

        var anaView = _engine.ProjectForParticipant(session, Ana)!;
        var biaView = _engine.ProjectForParticipant(session, Bia)!;
        const string Prompt = "Para um domingo sem planos, qual destas?";
        Assert.Equal([new RevealedCardView(SundayCard, Prompt, Picnic, NewRecipe, false)], anaView.Revealed);
        Assert.Equal([new RevealedCardView(SundayCard, Prompt, NewRecipe, Picnic, false)], biaView.Revealed);
        Assert.Equal((PowerCard, 2), (anaView.CurrentCard!.CardId, anaView.CurrentCard.Position));
        Assert.Null(anaView.MyAnswer);
        Assert.False(anaView.AwaitingPartner);
        Assert.Equal(SessionStatus.Active, session.Status);
    }

    [Fact]
    public void SameOptionIsMarkedAsChosenTogether()
    {
        var session = NewSession(_engine);
        session = Act(_engine, session, Bia, Choose(SundayCard, "street-fair"));
        session = Act(_engine, session, Ana, Choose(SundayCard, "street-fair"));

        var revealed = Assert.Single(_engine.ProjectForParticipant(session, Bia)!.Revealed);

        Assert.True(revealed.ChoseTheSame);
        Assert.Equal((StreetFair, StreetFair), (revealed.MyOptionLabel, revealed.PartnerOptionLabel));
    }

    public static TheoryData<ConstellationAction, ConstellationAction> SkipCombinations => new()
    {
        { Skip(SundayCard), Choose(SundayCard, "movies") },
        { Choose(SundayCard, "movies"), Skip(SundayCard) },
        { Skip(SundayCard), Skip(SundayCard) },
    };

    [Theory]
    [MemberData(nameof(SkipCombinations))]
    public void AnySkipPassesTheCardWithANeutralOutcomeForBoth(ConstellationAction byAna, ConstellationAction byBia)
    {
        var session = NewSession(_engine);
        session = Act(_engine, session, Ana, byAna);
        session = Act(_engine, session, Bia, byBia);

        foreach (var viewer in new[] { Ana, Bia })
        {
            var view = _engine.ProjectForParticipant(session, viewer)!;
            Assert.Empty(view.Revealed);
            Assert.Equal([new PassedCardView(SundayCard, "Para um domingo sem planos, qual destas?")], view.Passed);
            Assert.Equal(PowerCard, view.CurrentCard!.CardId);
            Assert.Null(view.MyAnswer);
        }
    }

    [Fact]
    public void AnsweringTheLastCardConcludesTheSessionEvenWhenItIsPassed()
    {
        var session = AtThirdCard(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, Skip(IceCreamCard));
        var applied = _engine.ApplyAction(Assert.IsType<Validation.Accepted>(
            _engine.ValidateAction(session, Command(session, Bia, Skip(IceCreamCard)))).Action);

        Assert.Equal(SessionStatus.Completed, applied.Session.Status);
        Assert.Equal(
            new SessionFinished(SessionStatus.Completed, applied.Session.Version, RoundStart),
            Assert.IsType<SessionFinished>(applied.Events[^1]));
        Assert.Equal(2, _engine.ProjectForParticipant(applied.Session, Bia)!.Passed.Count);
    }

    [Fact]
    public void EachAppliedActionIncrementsTheStateVersionByOne()
    {
        var session = Act(_engine, NewSession(_engine), Ana, Choose(SundayCard, "picnic"));
        Assert.Equal(new StateVersion(2), session.Version);

        session = Act(_engine, session, Bia, Skip(SundayCard));
        Assert.Equal(new StateVersion(3), session.Version);
    }

    [Fact]
    public void ResendingTheSameActionIdDoesNotRecordTheAnswerTwice()
    {
        var session = NewSession(_engine);
        var choose = Command(session, Ana, Choose(SundayCard, "picnic"));
        var afterFirst = Apply(_engine, session, choose);

        var resend = _engine.ValidateAction(afterFirst, choose);

        var replayed = Assert.IsType<Validation.AlreadyApplied>(resend);
        Assert.Equal(new ActionReceipt(choose.ActionId, new StateVersion(2)), replayed.Receipt);
        Assert.Equal(new StateVersion(2), afterFirst.Version);
        Assert.Equal(SundayCard, _engine.ProjectForParticipant(afterFirst, Bia)!.CurrentCard!.CardId);
    }

    [Fact]
    public void ResendOfTheRevealingChoiceAfterTheNextCardOpenedReturnsTheOriginalReceipt()
    {
        var session = Act(_engine, NewSession(_engine), Ana, Choose(SundayCard, "picnic"));
        var revealing = Command(session, Bia, Choose(SundayCard, "picnic"));
        session = Apply(_engine, session, revealing);
        session = Act(_engine, session, Ana, Choose(PowerCard, "perfect-toast"));

        var resend = _engine.ValidateAction(session, revealing);

        Assert.Equal(new StateVersion(3), Assert.IsType<Validation.AlreadyApplied>(resend).Receipt.ResourceVersion);
        Assert.Single(_engine.ProjectForParticipant(session, Bia)!.Revealed);
    }

    [Fact]
    public void ReusingAnActionIdWithADifferentAnswerIsRejected()
    {
        var session = NewSession(_engine);
        var choose = Command(session, Ana, Choose(SundayCard, "picnic"));
        session = Apply(_engine, session, choose);

        var reused = choose with { ExpectedVersion = session.Version, Action = Skip(SundayCard) };

        AssertRejected(RejectionReason.ActionIdReused, _engine.ValidateAction(session, reused));
    }

    [Fact]
    public void DivergentExpectedVersionIsATypedConflict()
    {
        var session = NewSession(_engine);
        var stale = Command(session, Bia, Choose(SundayCard, "movies"));
        session = Act(_engine, session, Ana, Choose(SundayCard, "picnic"));

        var conflict = Assert.IsType<Validation.Conflict>(_engine.ValidateAction(session, stale));

        Assert.Equal(new StateVersion(2), conflict.CurrentVersion);
    }

    [Fact]
    public void ExpectedVersionAheadOfTheServerIsAlsoAConflict()
    {
        var session = NewSession(_engine);
        var command = Command(session, Ana, Choose(SundayCard, "picnic")) with { ExpectedVersion = new StateVersion(2) };

        Assert.IsType<Validation.Conflict>(_engine.ValidateAction(session, command));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnsweringTheSameCardTwiceWithANewActionIdIsRejected(bool firstAnswerIsSkip)
    {
        var first = firstAnswerIsSkip ? Skip(SundayCard) : Choose(SundayCard, "picnic");
        var session = Act(_engine, NewSession(_engine), Ana, first);

        var again = _engine.ValidateAction(session, Command(session, Ana, Choose(SundayCard, "movies")));

        AssertRejected(RejectionReason.AlreadyActed, again);
    }

    [Theory]
    [InlineData(PowerCard)]
    [InlineData(IceCreamCard)]
    [InlineData("unknown-card")]
    public void AnsweringAnotherCardThanTheCurrentIsRejected(string cardId)
    {
        var session = NewSession(_engine);

        AssertRejected(RejectionReason.StaleStep, _engine.ValidateAction(session, Command(session, Ana, Skip(cardId))));
        AssertRejected(
            RejectionReason.StaleStep,
            _engine.ValidateAction(session, Command(session, Ana, Choose(cardId, "picnic"))));
    }

    [Fact]
    public void LateAnswerToTheCardThatJustClosedIsRejected()
    {
        var session = NewSession(_engine);
        session = Act(_engine, session, Ana, Choose(SundayCard, "picnic"));
        session = Act(_engine, session, Bia, Choose(SundayCard, "picnic"));

        var late = _engine.ValidateAction(session, Command(session, Ana, Choose(SundayCard, "movies")));

        AssertRejected(RejectionReason.StaleStep, late);
    }

    [Theory]
    [InlineData("perfect-toast")]
    [InlineData("not-an-option")]
    public void ChoosingAnOptionNotApprovedForTheCardIsRejected(string optionId)
    {
        var session = NewSession(_engine);

        var validation = _engine.ValidateAction(session, Command(session, Ana, Choose(SundayCard, optionId)));

        AssertRejected(RejectionReason.UnknownOption, validation);
    }

    [Fact]
    public void ActionAfterCompletionIsRejected()
    {
        var session = AtThirdCard(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, Skip(IceCreamCard));
        session = Act(_engine, session, Bia, Skip(IceCreamCard));

        var late = _engine.ValidateAction(session, Command(session, Ana, Skip(IceCreamCard)));

        AssertRejected(RejectionReason.SessionFinished, late);
    }

    [Fact]
    public void ActionFromSomeoneOutsideThePairIsRejected()
    {
        var session = NewSession(_engine);

        var validation = _engine.ValidateAction(session, Command(session, Outsider, Choose(SundayCard, "picnic")));

        AssertRejected(RejectionReason.NotParticipant, validation);
    }

    [Fact]
    public void RejectedActionDoesNotChangeTheSession()
    {
        var session = Act(_engine, NewSession(_engine), Ana, Choose(SundayCard, "picnic"));
        var before = _engine.ProjectForParticipant(session, Ana);

        _engine.ValidateAction(session, Command(session, Ana, Skip(SundayCard)));

        Assert.Equal(new StateVersion(2), session.Version);
        Assert.Equivalent(before, _engine.ProjectForParticipant(session, Ana), strict: true);
    }

    private static void AssertRejected(RejectionReason expected, Validation validation) =>
        Assert.Equal(expected, Assert.IsType<Validation.Rejected>(validation).Reason);
}
