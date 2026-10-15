using System.Text.Encodings.Web;
using System.Text.Json;
using Duora.Core.Experiences;
using Duora.Core.Experiences.Constellation;
using static Duora.UnitTests.Experiences.Constellation.ConstellationFixture;
using Outcome = Duora.Core.Experiences.DeadlineOutcome<
    Duora.Core.Experiences.Constellation.ConstellationState,
    Duora.Core.Experiences.Constellation.ConstellationAction>;

namespace Duora.UnitTests.Experiences.Constellation;

/// <summary>
/// INV-05 e spec 6.3: a primeira resposta nunca chega à projeção da outra pessoa, e o que não foi revelado por
/// dupla confirmação nunca sai. A asserção é sobre o JSON serializado, porque é ele que sai pela rede. As opções do
/// cartão aberto aparecem para os dois por regra, então a busca por uma escolha ignora o <c>CurrentCard</c>.
/// </summary>
public sealed class ConstellationPrivacyTests
{
    // O encoder padrão escapa acentos ("ã" vira "ã"); sem isto, procurar um rótulo passaria à toa.
    private static readonly JsonSerializerOptions PlainText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ManualTimeProvider _clock = ClockAtRoundStart();
    private readonly ConstellationEngine _engine;

    public ConstellationPrivacyTests() => _engine = Engine(_clock);

    [Fact]
    public void FirstChoiceIsAbsentFromThePartnersJsonUntilDoubleConfirmation()
    {
        var session = AtThirdCard(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, Choose(IceCreamCard, "cinnamon-coffee"));

        var biaBefore = SerializeWithoutCurrentCard(session, Bia);
        var anaOwn = SerializeWithoutCurrentCard(session, Ana);

        Assert.DoesNotContain(CinnamonCoffee, biaBefore, StringComparison.Ordinal);
        Assert.DoesNotContain("cinnamon-coffee", biaBefore, StringComparison.Ordinal);
        // Sentinelas: a escolha está serializada na projeção de Ana e chega a Bia depois da dupla confirmação; sem
        // elas, as duas asserções acima passariam à toa se a busca ou a serialização falhassem.
        Assert.Contains(CinnamonCoffee, anaOwn, StringComparison.Ordinal);
        session = Act(_engine, session, Bia, Choose(IceCreamCard, "corn-cake"));
        Assert.Contains($"\"PartnerOptionLabel\":\"{CinnamonCoffee}\"", Serialize(session, Bia), StringComparison.Ordinal);
    }

    public static TheoryData<ParticipantId, string, ConstellationAction> FirstAnswers => new()
    {
        { Ana, SundayCard, Choose(SundayCard, "picnic") },
        { Ana, SundayCard, Skip(SundayCard) },
        { Ana, IceCreamCard, Choose(IceCreamCard, "popcorn-caramel") },
        { Ana, IceCreamCard, Skip(IceCreamCard) },
        { Bia, SundayCard, Choose(SundayCard, "movies") },
        { Bia, SundayCard, Skip(SundayCard) },
        { Bia, IceCreamCard, Choose(IceCreamCard, "cinnamon-coffee") },
        { Bia, IceCreamCard, Skip(IceCreamCard) },
    };

    [Theory]
    [MemberData(nameof(FirstAnswers))]
    public void FirstAnswerChangesNothingInThePartnersProjectionButTheVersion(
        ParticipantId firstToAnswer, string cardId, ConstellationAction answer)
    {
        var session = cardId == SundayCard ? NewSession(_engine) : AtThirdCard(_engine, NewSession(_engine));
        var partner = firstToAnswer == Ana ? Bia : Ana;
        var before = _engine.ProjectForParticipant(session, partner)!;

        session = Act(_engine, session, firstToAnswer, answer);

        var after = _engine.ProjectForParticipant(session, partner)!;
        Assert.Equal(before.ResourceVersion + 1, after.ResourceVersion);
        Assert.Equal(
            JsonSerializer.Serialize(before with { ResourceVersion = 0 }, PlainText),
            JsonSerializer.Serialize(after with { ResourceVersion = 0 }, PlainText));
    }

    [Fact]
    public void ChoiceOnASkippedCardNeverReachesEitherProjectionUntilTheEnd()
    {
        var session = NewSession(_engine);
        session = Act(_engine, session, Ana, Choose(SundayCard, "street-fair"));
        session = Act(_engine, session, Bia, Choose(SundayCard, "street-fair"));
        session = Act(_engine, session, Ana, Choose(PowerCard, "song-lyrics"));
        List<ConstellationSession> laterSessions = [];
        session = Act(_engine, session, Bia, Skip(PowerCard));
        laterSessions.Add(session);
        session = Act(_engine, session, Bia, Skip(IceCreamCard));
        laterSessions.Add(session);
        session = Act(_engine, session, Ana, Choose(IceCreamCard, "corn-cake"));
        laterSessions.Add(session);

        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.All(laterSessions, later =>
        {
            foreach (var viewer in new[] { Ana, Bia })
            {
                var json = SerializeWithoutCurrentCard(later, viewer);
                Assert.DoesNotContain(SongLyrics, json, StringComparison.Ordinal);
                Assert.DoesNotContain(CornCake, json, StringComparison.Ordinal);
                // Sentinela: o cartão revelado antes continua no resumo.
                Assert.Contains(StreetFair, json, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void ExpiredSessionDropsTheUnrevealedChoiceFromBothProjections()
    {
        var session = AtThirdCard(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, Choose(IceCreamCard, "popcorn-caramel"));
        _clock.SetUtcNow(Deadline);
        var expired = Assert.IsType<Outcome.Resolved>(_engine.HandleDeadline(session, session.Version)).Session;

        var anaJson = Serialize(expired, Ana);
        var biaJson = Serialize(expired, Bia);

        Assert.DoesNotContain(PopcornCaramel, anaJson, StringComparison.Ordinal);
        Assert.DoesNotContain(PopcornCaramel, biaJson, StringComparison.Ordinal);
        Assert.Contains(StreetFair, anaJson, StringComparison.Ordinal);
        Assert.Contains(StreetFair, biaJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionExposesOnlyTheAllowedFields()
    {
        var session = NewSession(_engine);

        using var document = JsonDocument.Parse(Serialize(session, Ana));

        string[] expected =
        [
            "ExperienceId", "RulesVersion", "ContentVersion", "SchemaVersion", "ResourceVersion", "Status", "Deadline",
            "Stage", "Title", "Objective", "CardCount", "CurrentCard", "MyAnswer", "AwaitingPartner", "Revealed",
            "Passed", "AvailableActions",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), KeysOf(document.RootElement));
    }

    [Fact]
    public void NestedObjectsExposeOnlyTheAllowedFields()
    {
        var session = AtThirdCard(_engine, NewSession(_engine));
        session = Act(_engine, session, Ana, Choose(IceCreamCard, "corn-cake"));

        using var document = JsonDocument.Parse(Serialize(session, Ana));
        var root = document.RootElement;

        Assert.Equal(["CardId", "Options", "Position", "Prompt"], KeysOf(root.GetProperty("CurrentCard")));
        Assert.All(root.GetProperty("CurrentCard").GetProperty("Options").EnumerateArray(),
            option => Assert.Equal(["Label", "OptionId"], KeysOf(option)));
        Assert.Equal(["Kind", "Option"], KeysOf(root.GetProperty("MyAnswer")));
        Assert.All(root.GetProperty("Revealed").EnumerateArray(), card => Assert.Equal(
            ["CardId", "ChoseTheSame", "MyOptionLabel", "PartnerOptionLabel", "Prompt"], KeysOf(card)));
        Assert.All(root.GetProperty("Passed").EnumerateArray(),
            card => Assert.Equal(["CardId", "Prompt"], KeysOf(card)));
        Assert.Single(root.GetProperty("Revealed").EnumerateArray());
        Assert.Single(root.GetProperty("Passed").EnumerateArray());
    }

    [Fact]
    public void ProjectionDoesNotCarryParticipantOrActionIds()
    {
        var session = NewSession(_engine);
        var actionId = new ActionId(Guid.Parse("0199d700-7f20-7000-8000-0000000000a1"));
        session = Apply(_engine, session, Command(session, Ana, Choose(SundayCard, "picnic"), actionId));
        session = Act(_engine, session, Bia, Choose(SundayCard, "movies"));

        var json = Serialize(session, Ana) + Serialize(session, Bia);

        Assert.DoesNotContain(actionId.Value.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Ana.Value.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Bia.Value.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SomeoneOutsideThePairGetsNoProjection()
    {
        var session = Act(_engine, NewSession(_engine), Ana, Choose(SundayCard, "picnic"));

        Assert.Null(_engine.ProjectForParticipant(session, Outsider));
    }

    private static string[] KeysOf(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    private string Serialize(ConstellationSession session, ParticipantId viewer) =>
        JsonSerializer.Serialize(_engine.ProjectForParticipant(session, viewer), PlainText);

    /// <summary>O JSON da projeção sem o cartão aberto, cujas opções os dois veem por regra.</summary>
    private string SerializeWithoutCurrentCard(ConstellationSession session, ParticipantId viewer)
    {
        var node = JsonSerializer.SerializeToNode(_engine.ProjectForParticipant(session, viewer), PlainText)!.AsObject();
        Assert.True(node.Remove("CurrentCard"));
        return node.ToJsonString(PlainText);
    }
}
