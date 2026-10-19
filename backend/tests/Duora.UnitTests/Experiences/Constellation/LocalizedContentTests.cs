using Duora.Core.Experiences;
using Duora.Core.Experiences.Constellation;
using static Duora.UnitTests.Experiences.Constellation.ConstellationFixture;

namespace Duora.UnitTests.Experiences.Constellation;

public sealed class LocalizedContentTests
{
    [Fact]
    public void ProjectionChangesTextButKeepsIdsAndHidesPartnerAnswer()
    {
        var en = new ConstellationContent(1, "A Sunday constellation", "Choose together", [
            new ConstellationCard(SundayCard, "What would you do on Sunday?", [
                new ConstellationOption("picnic", "Picnic"), new ConstellationOption("movies", "Movies")])]);
        var pt = new ConstellationContent(1, "Constelação de domingo", "Escolham juntos", [
            new ConstellationCard(SundayCard, "O que faria no domingo?", [
                new ConstellationOption("picnic", "Piquenique"), new ConstellationOption("movies", "Cinema")])]);
        var localized = new LocalizedConstellationContent(en, pt);
        var engine = Engine(ClockAtRoundStart());
        var session = Act(engine, NewSession(engine, en), Ana, Choose(SundayCard, "picnic"));
        var english = localized.Project(session, Bia, "en-US")!;
        var portuguese = localized.Project(session, Bia, "pt-BR")!;
        Assert.Equal(english.CurrentCard!.CardId, portuguese.CurrentCard!.CardId);
        Assert.NotEqual(english.CurrentCard.Prompt, portuguese.CurrentCard.Prompt);
        Assert.Null(portuguese.MyAnswer);
        Assert.Empty(portuguese.Revealed);
        Assert.Null(localized.Project(session, Outsider, "pt-BR"));
        Assert.Throws<ArgumentException>(() => new LocalizedConstellationContent(en, new ConstellationContent(2, pt.Title, pt.Objective, pt.Cards)));
    }

    [Fact]
    public void SnapshotRoundTripPreservesPrivateAnswerAndReplay()
    {
        var engine = Engine(ClockAtRoundStart());
        var session = NewSession(engine);
        var command = Command(session, Ana, Choose(SundayCard, "picnic"));
        session = Apply(engine, session, command);
        var json = System.Text.Json.JsonSerializer.Serialize(ConstellationSnapshot.Capture(session));
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<ConstellationSnapshot>(json)!;
        var restored = snapshot.Restore(ContentV1());
        Assert.Equal("picnic", engine.ProjectForParticipant(restored, Ana)!.MyAnswer!.Option!.OptionId);
        Assert.Null(engine.ProjectForParticipant(restored, Bia)!.MyAnswer);
        Assert.IsType<ActionValidation<ConstellationState, ConstellationAction>.AlreadyApplied>(engine.ValidateAction(restored, command));
        Assert.Throws<ArgumentException>(() => snapshot.Restore(Content(2, "Revised")));
    }

    [Fact]
    public void PublishedContentCollectionsCannotBeMutated()
    {
        var content = ContentV1();
        Assert.Throws<NotSupportedException>(() => ((IList<ConstellationCard>)content.Cards)[0] = content.Cards[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<ConstellationOption>)content.Cards[0].Options)[0] = content.Cards[0].Options[1]);
    }

    [Fact]
    public void SnapshotRejectsFinishedSessionWithPrivateAnswer()
    {
        var engine = Engine(ClockAtRoundStart());
        var session = Act(engine, NewSession(engine), Ana, Choose(SundayCard, "picnic"));
        var snapshot = ConstellationSnapshot.Capture(session);
        var corrupted = snapshot with { Control = snapshot.Control with { Status = SessionStatus.Completed } };
        Assert.Throws<ArgumentException>(() => corrupted.Restore(ContentV1()));
    }

    [Fact]
    public void SnapshotRoundTripPreservesRevealedAndPassedCards()
    {
        var engine = Engine(ClockAtRoundStart());
        var session = AtThirdCard(engine, NewSession(engine));
        var snapshot = ConstellationSnapshot.Capture(session);
        var restored = snapshot.Restore(ContentV1());
        var view = engine.ProjectForParticipant(restored, Bia)!;
        Assert.Equal(StreetFair, Assert.Single(view.Revealed).MyOptionLabel);
        Assert.Equal(PowerCard, Assert.Single(view.Passed).CardId);
        Assert.Null(view.MyAnswer);
        Assert.Equal(session.Version, restored.Version);
    }
}
