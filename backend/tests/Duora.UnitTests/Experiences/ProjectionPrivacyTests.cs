using System.Text.Encodings.Web;
using System.Text.Json;
using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;

namespace Duora.UnitTests.Experiences;

/// <summary>
/// INV-05: a projeção de um participante nunca carrega as pistas do outro nem o estado interno. A asserção é
/// sobre o JSON serializado, porque é ele que sai pela rede.
/// </summary>
public sealed class ProjectionPrivacyTests
{
    private static readonly string[] AnaClues = [AnaClueOne, AnaClueTwo];
    private static readonly string[] BiaClues = [BiaClueOne, BiaClueTwo];

    // O encoder padrão escapa acentos ("ç" vira "ç"); sem isto, procurar o texto da pista passaria à toa.
    private static readonly JsonSerializerOptions PlainText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly UnlikelyRoutesEngine _engine = Engine(ClockAtRoundStart());

    [Fact]
    public void ParticipantNeverReceivesThePartnersCluesInAnyStage()
    {
        foreach (var session in EveryStageOfACompleteSession())
        {
            var anaJson = Serialize(session, Ana);
            var biaJson = Serialize(session, Bia);

            Assert.All(BiaClues, clue => Assert.DoesNotContain(clue, anaJson, StringComparison.Ordinal));
            Assert.All(AnaClues, clue => Assert.DoesNotContain(clue, biaJson, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ParticipantReceivesTheirOwnCluesOnceCluesAreDealt()
    {
        var session = AtChoices(_engine, NewSession(_engine));

        var anaJson = Serialize(session, Ana);
        var biaJson = Serialize(session, Bia);

        // Sentinela: se a serialização perdesse as pistas, o teste acima passaria à toa.
        Assert.All(AnaClues, clue => Assert.Contains(clue, anaJson, StringComparison.Ordinal));
        Assert.All(BiaClues, clue => Assert.Contains(clue, biaJson, StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectionExposesOnlyTheAllowedFields()
    {
        var session = AtChoices(_engine, NewSession(_engine));

        using var document = JsonDocument.Parse(Serialize(session, Ana));

        string[] expected =
        [
            "ExperienceId", "RulesVersion", "ContentVersion", "SchemaVersion", "ResourceVersion", "Status", "Deadline",
            "Stage", "Title", "Objective", "Example", "MyClues", "CurrentStop", "IsMyTurn", "Route",
            "RevisionsRemaining", "AwaitingPartner", "AvailableActions",
        ];
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NestedObjectsExposeOnlyTheAllowedFields()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));

        using var document = JsonDocument.Parse(Serialize(session, Bia));
        var root = document.RootElement;

        Assert.Equal(["Options", "Prompt", "StepId"], KeysOf(root.GetProperty("CurrentStop")));
        Assert.All(root.GetProperty("CurrentStop").GetProperty("Options").EnumerateArray(),
            option => Assert.Equal(["Label", "OptionId"], KeysOf(option)));
        var route = root.GetProperty("Route").EnumerateArray().ToList();
        Assert.Single(route);
        Assert.All(route, stop => Assert.Equal(["OptionLabel", "Prompt", "StepId"], KeysOf(stop)));
    }

    [Fact]
    public void ProjectionDoesNotCarryTheIdsOfAppliedActions()
    {
        var session = NewSession(_engine);
        ActionId[] actionIds =
        [
            new(Guid.Parse("0199d700-7f20-7000-8000-0000000000a1")),
            new(Guid.Parse("0199d700-7f20-7000-8000-0000000000b2")),
        ];
        session = Apply(_engine, session, Command(
            session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction), actionIds[0]));
        session = Apply(_engine, session, Command(
            session, Bia, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction), actionIds[1]));

        var json = Serialize(session, Ana) + Serialize(session, Bia);

        Assert.All(actionIds, id => Assert.DoesNotContain(id.Value.ToString(), json, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProjectionDoesNotCarryParticipantIdsSeedOrActionLog()
    {
        var session = AtChoices(_engine, NewSession(_engine, seed: new SessionSeed(1_234_567)));

        var json = Serialize(session, Ana);

        Assert.DoesNotContain(Ana.Value.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Bia.Value.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1234567", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SomeoneOutsideThePairGetsNoProjection()
    {
        var session = AtChoices(_engine, NewSession(_engine));

        Assert.Null(_engine.ProjectForParticipant(session, Outsider));
    }

    [Fact]
    public void ExpiredSessionStillHidesThePartnersClues()
    {
        var clock = ClockAtRoundStart();
        var engine = Engine(clock);
        var session = AtChoices(engine, NewSession(engine));
        clock.Advance(TimeSpan.FromMinutes(8));
        var expired = Assert.IsType<DeadlineOutcome<UnlikelyRoutesState, UnlikelyRoutesAction>.Resolved>(
            engine.HandleDeadline(session, session.Version)).Session;

        var anaJson = JsonSerializer.Serialize(engine.ProjectForParticipant(expired, Ana), PlainText);
        Assert.Contains(AnaClueOne, anaJson, StringComparison.Ordinal);

        Assert.All(BiaClues, clue => Assert.DoesNotContain(clue, anaJson, StringComparison.Ordinal));
    }

    private static string[] KeysOf(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    private string Serialize(UnlikelyRoutesSession session, ParticipantId viewer) =>
        JsonSerializer.Serialize(_engine.ProjectForParticipant(session, viewer), PlainText);

    private IEnumerable<UnlikelyRoutesSession> EveryStageOfACompleteSession()
    {
        var session = NewSession(_engine);
        yield return session;

        UnlikelyRoutesAction[] script =
        [
            new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction),
            new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction),
            new UnlikelyRoutesAction.Acknowledge(RoutesStage.Clues),
            new UnlikelyRoutesAction.Acknowledge(RoutesStage.Clues),
            new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"),
            new UnlikelyRoutesAction.ChooseOption("route-stop-2", "tram-station"),
            new UnlikelyRoutesAction.ChooseOption("route-stop-3", "map-fair"),
            new UnlikelyRoutesAction.ChooseOption("route-stop-4", "cloud-observatory"),
            new UnlikelyRoutesAction.ConfirmRoute(),
            new UnlikelyRoutesAction.ConfirmRoute(),
        ];
        for (var step = 0; step < script.Length; step++)
        {
            var actor = step % 2 == 0 ? Ana : Bia;
            session = Act(_engine, session, actor, script[step]);
            yield return session;
        }

        Assert.Equal(SessionStatus.Completed, session.Status);
    }
}
