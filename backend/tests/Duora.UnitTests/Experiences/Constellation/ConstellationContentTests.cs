using Duora.Core.Experiences;
using Duora.Core.Experiences.Constellation;
using static Duora.UnitTests.Experiences.Constellation.ConstellationFixture;

namespace Duora.UnitTests.Experiences.Constellation;

/// <summary>Conteúdo aprovado, versionado e imutável (spec 6.3) e INV-09 no domínio.</summary>
public sealed class ConstellationContentTests
{
    private readonly ConstellationEngine _engine = Engine(ClockAtRoundStart());

    [Fact]
    public void SessionKeepsTheContentVersionItWasCreatedWithAcrossCards()
    {
        var session = NewSession(_engine, Content(contentVersion: 1, title: "Constelação de domingo"));

        session = AtThirdCard(_engine, session);

        var view = _engine.ProjectForParticipant(session, Bia)!;
        Assert.Equal(1, session.Experience.ContentVersion);
        Assert.Equal((1, "Constelação de domingo"), (view.ContentVersion, view.Title));
    }

    [Fact]
    public void SessionsOfDifferentContentVersionsProjectTheirOwnCards()
    {
        var older = NewSession(_engine, ContentV1());
        var newer = NewSession(_engine, new ConstellationContent(2, "Constelação revisada", "Cartões novos.",
        [
            new ConstellationCard("rainy-day", "Num dia de chuva, qual destas?",
            [
                new ConstellationOption("board-games", "Jogos de tabuleiro"),
                new ConstellationOption("long-nap", "Uma soneca longa"),
            ]),
        ]));

        older = Act(_engine, older, Ana, Skip(SundayCard));

        var olderView = _engine.ProjectForParticipant(older, Ana)!;
        var newerView = _engine.ProjectForParticipant(newer, Ana)!;
        Assert.Equal((1, SundayCard, 3), (olderView.ContentVersion, olderView.CurrentCard!.CardId, olderView.CardCount));
        Assert.Equal((2, "rainy-day", 1), (newerView.ContentVersion, newerView.CurrentCard!.CardId, newerView.CardCount));
    }

    [Fact]
    public void SessionRecordsEveryVersionOfTheExperience()
    {
        var session = NewSession(_engine, Content(contentVersion: 3, title: "Terceira"));

        Assert.Equal(
            new ExperienceVersion(
                new ExperienceId("choice-constellation"), rulesVersion: 1, contentVersion: 3, schemaVersion: 1),
            session.Experience);
    }

    [Fact]
    public void ContentDoesNotChangeWhenTheSourceListChangesLater()
    {
        List<ConstellationCard> cards = [.. ContentV1().Cards];
        var content = new ConstellationContent(1, "Título", "Objetivo.", cards);

        cards.Clear();

        Assert.Equal(3, content.Cards.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ContentVersionMustBePositive(int contentVersion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConstellationContent(contentVersion, "Título", "Objetivo.", ContentV1().Cards));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ConstellationContent.MaxCards + 1)]
    public void ContentNeedsBetweenOneAndMaxCards(int cardCount)
    {
        var cards = Enumerable.Range(1, cardCount).Select(index => Card($"card-{index}")).ToList();

        Assert.Throws<ArgumentException>(() => new ConstellationContent(1, "Título", "Objetivo.", cards));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(ConstellationContent.MaxCards)]
    public void ContentAcceptsTheCardCountBoundaries(int cardCount)
    {
        var cards = Enumerable.Range(1, cardCount).Select(index => Card($"card-{index}")).ToList();

        Assert.Equal(cardCount, new ConstellationContent(1, "Título", "Objetivo.", cards).Cards.Count);
    }

    [Fact]
    public void CardIdsMustBeUnique()
    {
        Assert.Throws<ArgumentException>(
            () => new ConstellationContent(1, "Título", "Objetivo.", [Card("same"), Card("same")]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(ConstellationCard.MaxOptions + 1)]
    public void CardNeedsBetweenTwoAndMaxOptions(int optionCount)
    {
        var options = Enumerable.Range(1, optionCount).Select(index => new ConstellationOption($"o-{index}", "Opção"));

        Assert.Throws<ArgumentException>(() => new ConstellationCard("card", "Pergunta?", [.. options]));
    }

    [Fact]
    public void OptionIdsMustBeUniqueInTheCard()
    {
        Assert.Throws<ArgumentException>(() => new ConstellationCard("card", "Pergunta?",
            [new ConstellationOption("same", "Uma"), new ConstellationOption("same", "Outra")]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Com Maiúscula")]
    [InlineData("-hifen-na-ponta")]
    public void CardAndOptionIdsMustBeSlugs(string id)
    {
        Assert.Throws<ArgumentException>(() => Card(id));
        Assert.Throws<ArgumentException>(() => new ConstellationOption(id, "Opção"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Texto com \u0000 NUL")]
    public void TextsMustBeVisibleAndFreeOfControlCharacters(string text)
    {
        Assert.Throws<ArgumentException>(() => new ConstellationOption("option", text));
        Assert.Throws<ArgumentException>(() => new ConstellationContent(1, text, "Objetivo.", [Card("card")]));
    }

    [Fact]
    public void TextsLongerThanTheLimitAreRejected()
    {
        var tooLong = new string('a', ConstellationContent.MaxTextLength + 1);
        var atLimit = new string('a', ConstellationContent.MaxTextLength);

        Assert.Throws<ArgumentException>(() => new ConstellationOption("option", tooLong));
        Assert.Equal(atLimit, new ConstellationOption("option", atLimit).Label);
    }

    private static ConstellationCard Card(string cardId) => new(cardId, "Pergunta?",
        [new ConstellationOption("one", "Uma"), new ConstellationOption("two", "Outra")]);
}
