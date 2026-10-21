using System.Security.Cryptography;
using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;

namespace Duora.UnitTests.Experiences;

public sealed class LocalizedRoutesAndAudioTests
{
    [Fact]
    public void RoutesLocalizeOwnCluesAndPreserveOptionIds()
    {
        var en = new UnlikelyRoutesContent(2, "A walk", "Choose a route", "Start by the water", [
            new RouteStop("start", "Where first?", [new RouteOption("park", "Park"), new RouteOption("lake", "Lake")])],
            [[new Clue("Start early")], [new Clue("Finish on a hill")]]);
        var pt = new UnlikelyRoutesContent(2, "Um passeio", "Escolham um roteiro", "Comecem junto da água", [
            new RouteStop("start", "Por onde começar?", [new RouteOption("park", "Parque"), new RouteOption("lake", "Lago")])],
            [[new Clue("Comecem cedo")], [new Clue("Terminem numa colina")]]);
        var catalog = new LocalizedUnlikelyRoutesContent(en, pt);
        var engine = Engine(ClockAtRoundStart());
        var session = NewSession(engine, en);
        Assert.Empty(catalog.Project(session, Ana, "en-US")!.MyClues);
        Assert.Empty(catalog.Project(session, Ana, "pt-BR")!.MyClues);
        session = Act(engine, session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        Assert.Empty(catalog.Project(session, Ana, "en-US")!.MyClues);
        session = Act(engine, session, Bia, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        var english = catalog.Project(session, Ana, "en-US")!;
        var portuguese = catalog.Project(session, Ana, "pt-BR")!;
        Assert.Equal("Start early", Assert.Single(english.MyClues));
        Assert.Equal("Comecem cedo", Assert.Single(portuguese.MyClues));
        Assert.Equal(english.ContentVersion, portuguese.ContentVersion);
        Assert.Null(catalog.Project(session, Outsider, "pt-BR"));
        var bad = new UnlikelyRoutesContent(2, pt.Title, pt.Objective, pt.Example, [
            new RouteStop("different", "Onde?", pt.Stops[0].Options)], pt.ClueSets);
        Assert.Throws<ArgumentException>(() => new LocalizedUnlikelyRoutesContent(en, bad));
    }

    [Fact]
    public void AudioPublicationRequiresBothTranscriptsProvenanceAndVerifiedBytes()
    {
        // Synthetic offline bytes, not publication-ready audio.
        byte[] bytes = [82, 73, 70, 70, 1, 2, 3];
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var en = new LocalizedAudioVariant("intro-en", "Welcome", "A welcome voice", "Synthetic test fixture", hash, bytes);
        var pt = new LocalizedAudioVariant("intro-pt", "Bem-vindos", "Uma voz de boas-vindas", "Amostra sintética de teste", hash, bytes);
        var cue = new LocalizedAudio("intro", en, pt);
        Assert.Equal("Bem-vindos", cue.ForLocale("pt-BR").Transcript);
        Assert.Equal("intro-pt", cue.ForLocale("pt-BR").AssetId);
        Assert.DoesNotContain("intro-en", System.Text.Json.JsonSerializer.Serialize(cue.ForLocale("pt-BR")));
        Assert.Throws<ArgumentException>(() => cue.ForLocale("es"));
        Assert.Throws<ArgumentNullException>(() => new LocalizedAudio("intro", en, null!));
        Assert.Throws<ArgumentException>(() => new LocalizedAudioVariant("intro-en", "", "Voice", "Fixture", hash, bytes));
        Assert.Throws<ArgumentException>(() => new LocalizedAudioVariant("intro-en", "Welcome", "Voice", "Fixture", hash, new byte[] { 0 }));
        Assert.Throws<ArgumentException>(() => new LocalizedAudioVariant("intro-en", "Welcome", "Voice", "", hash, bytes));
    }

    [Fact]
    public void AccessibleInstructionsHaveSameActionsAndStagesInBothLocales()
    {
        var routes = new UnlikelyRoutesRules(new UnlikelyRoutesSettings(1));
        var en = routes.DescribeAccessibleInteraction("en-US");
        var pt = routes.DescribeAccessibleInteraction("pt-BR");
        Assert.Equal(pt.Stages.Select(s => s.StageId), en.Stages.Select(s => s.StageId));
        Assert.Equal(pt.Actions.Select(a => a.Type), en.Actions.Select(a => a.Type));
        Assert.Contains("keyboard", en.Summary);
        Assert.NotEqual(pt.Summary, en.Summary);
        var cards = new Duora.Core.Experiences.Constellation.ConstellationRules();
        Assert.Contains("keyboard", cards.DescribeAccessibleInteraction("en-US").Summary);
        Assert.Equal(cards.DescribeAccessibleInteraction().Summary, cards.DescribeAccessibleInteraction("pt-BR").Summary);
    }
}
