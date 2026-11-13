using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Duora.Contracts.Participation;
using Duora.Core.Experiences.Constellation;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// INV-05 pela borda HTTP real (T-05): o JSON da sala tem exatamente as chaves permitidas em cada estado e não carrega
/// resposta, pista ou identidade da outra pessoa, nem nos erros. As listas de chaves são literais de propósito: regenerar
/// o contrato não pode bastar para um campo novo passar.
/// </summary>
public sealed class RoomResponseAllowlistTests(PostgresFixture postgres)
{
    private const string ConstellationInvited = "constellation-invited";
    private const string ConstellationAnswered = "constellation-answered-before-reveal";
    private const string ConstellationRevealed = "constellation-after-first-reveal";
    private const string ConstellationPassed = "constellation-after-skipped-card";
    private const string ConstellationPassedByBob = "constellation-after-skipped-card-by-bob";
    private const string ConstellationCompleted = "constellation-completed";
    private const string RoutesInvited = "routes-invited";
    private const string RoutesIntroduction = "routes-introduction";
    private const string RoutesClues = "routes-clues";
    private const string RoutesChoosing = "routes-choosing";
    private const string RoutesCompleted = "routes-completed";

    private const string FirstCard = "dawn-card";
    private const string SecondCard = "dusk-card";
    private const string AliceFirstOption = "ember-alpha";
    private const string BobFirstOption = "river-beta";
    private const string AliceFirstLabel = "Ember Alpha";
    private const string AliceFirstLabelPt = "Brasa Alfa";
    private const string BobFirstLabel = "River Beta";
    private const string BobFirstLabelPt = "Rio Beta";
    private const string FirstPrompt = "First light?";
    private const string FirstPromptPt = "Primeira luz?";
    private const string FirstStop = "stop";
    private const string SecondStop = "stop-second";
    private const string ChooseOption = "choose-option";
    private const string SkipCard = "skip-card";
    private const string Acknowledge = "acknowledge";
    private const string ConfirmRoute = "confirm-route";
    private const string VersionKey = "version";
    private const string GameKey = "game";
    private const string ResourceVersionKey = "resourceVersion";
    private const string CurrentCardKey = "currentCard";
    private const string PassedKey = "passed";
    private const string PromptKey = "prompt";

    private static readonly string[] None = [];

    private static readonly string[] Room =
    [
        "roomId", "state", VersionKey, "communicationLanguage", "communicationPolicyVersion", "displayLocale",
        "invitationExpiresAt", GameKey, "audio", "routesGame",
    ];

    private static readonly string[] Audio =
    [
        "audio[].cueId", "audio[].assetId", "audio[].transcript", "audio[].accessibleDescription", "audio[].sha256", "audio[].url",
    ];

    private static readonly string[] ConstellationGame =
    [
        "game.experienceId", "game.rulesVersion", "game.contentVersion", "game.schemaVersion", "game.resourceVersion",
        "game.status", "game.deadline", "game.stage", "game.title", "game.objective", "game.cardCount", "game.currentCard",
        "game.myAnswer", "game.awaitingPartner", "game.revealed", "game.passed", "game.availableActions",
    ];

    private static readonly string[] OpenCard =
    [
        "game.currentCard.cardId", "game.currentCard.position", "game.currentCard.prompt", "game.currentCard.options",
        "game.currentCard.options[].optionId", "game.currentCard.options[].label",
    ];

    private static readonly string[] OwnAnswer =
    [
        "game.myAnswer.kind", "game.myAnswer.option", "game.myAnswer.option.optionId", "game.myAnswer.option.label",
    ];

    private static readonly string[] RevealedCard =
    [
        "game.revealed[].cardId", "game.revealed[].prompt", "game.revealed[].myOptionLabel",
        "game.revealed[].partnerOptionLabel", "game.revealed[].choseTheSame",
    ];

    private static readonly string[] PassedCard = ["game.passed[].cardId", "game.passed[].prompt"];

    private static readonly string[] RoutesGame =
    [
        "routesGame.experienceId", "routesGame.rulesVersion", "routesGame.contentVersion", "routesGame.schemaVersion",
        "routesGame.resourceVersion", "routesGame.status", "routesGame.deadline", "routesGame.stage", "routesGame.title",
        "routesGame.objective", "routesGame.example", "routesGame.myClues", "routesGame.currentStop", "routesGame.isMyTurn",
        "routesGame.route", "routesGame.revisionsRemaining", "routesGame.awaitingPartner", "routesGame.availableActions",
    ];

    private static readonly string[] OpenStop =
    [
        "routesGame.currentStop.stepId", "routesGame.currentStop.prompt", "routesGame.currentStop.options",
        "routesGame.currentStop.options[].optionId", "routesGame.currentStop.options[].label",
    ];

    private static readonly string[] ChosenStop =
    [
        "routesGame.route[].stepId", "routesGame.route[].prompt", "routesGame.route[].optionLabel",
    ];

    private static readonly string[] Problem = ["type", "title", "status", "code", "traceId"];

    private static readonly string EnglishSecondCardText = LocalizedText(
        "en-US", "Shared choices", "Explore choices together.", "Last light?", "Cedar Gamma", "Harbor Delta");

    private static readonly string PortugueseSecondCardText = LocalizedText(
        "pt-BR", "Escolhas em comum", "Explore escolhas em dupla.", "Última luz?", "Cedro Gama", "Porto Delta");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ConstellationInvited, "Invited")]
    [InlineData(ConstellationAnswered, "Active")]
    [InlineData(ConstellationRevealed, "Active")]
    [InlineData(ConstellationPassed, "Active")]
    [InlineData(ConstellationPassedByBob, "Active")]
    [InlineData(ConstellationCompleted, "Completed")]
    [InlineData(RoutesInvited, "Invited")]
    [InlineData(RoutesIntroduction, "Active")]
    [InlineData(RoutesClues, "Active")]
    [InlineData(RoutesChoosing, "Active")]
    [InlineData(RoutesCompleted, "Completed")]
    public async Task RoomResponseExposesOnlyAllowedFieldsInEachState(string stage, string state)
    {
        await using var scene = await SceneAsync(stage);

        foreach (var viewerIsAlice in new[] { true, false })
        {
            using var response = await (viewerIsAlice ? scene.Alice : scene.Bob).GetAsync(scene.RoomPath);
            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = JsonNode.Parse(body)!.AsObject();
            Assert.Equal(state, (string?)json["state"]);
            Assert.Equal(Sorted(ExpectedKeys(stage, viewerIsAlice)), Sorted(KeysOf(json)));
            AssertAbsent(FullText(response, body), scene.PartnerCanaries(viewerIsAlice));
            if (stage == ConstellationAnswered && !viewerIsAlice)
            {
                AssertAbsent(WithoutOpenCard(json), AliceFirstOption, AliceFirstLabel, AliceFirstLabelPt);
            }
        }
    }

    [Fact]
    public async Task ThePartnerAnsweringChangesOnlyTheVersionsInTheOtherView()
    {
        await using var scene = await SceneAsync(ConstellationInvited);
        await scene.AcceptBothAsync();
        var before = await ReadRoomAsync(scene.Bob, scene.RoomPath);

        await scene.ChooseAsync(scene.Seed.Alice, scene.AliceAction, FirstCard, AliceFirstOption);

        var after = await ReadRoomAsync(scene.Bob, scene.RoomPath);
        Assert.NotEqual((string?)before[VersionKey], (string?)after[VersionKey]);
        Assert.Equal(WithoutVersions(before), WithoutVersions(after));
    }

    [Theory]
    [InlineData(ConstellationPassed)]
    [InlineData(ConstellationPassedByBob)]
    public async Task APassedCardDoesNotIdentifyWhoSkipped(string stage)
    {
        await using var scene = await SceneAsync(stage);

        var alice = await ReadRoomAsync(scene.Alice, scene.RoomPath);
        var bob = await ReadRoomAsync(scene.Bob, scene.RoomPath);

        Assert.Equal(PassedCardJson(FirstPrompt), alice[GameKey]![PassedKey]!.ToJsonString());
        Assert.Equal(PassedCardJson(FirstPromptPt), bob[GameKey]![PassedKey]!.ToJsonString());
        Assert.Equal(EnglishSecondCardText, LocalizedFields(alice));
        Assert.Equal(PortugueseSecondCardText, LocalizedFields(bob));
        Assert.Equal(WithoutLocalizedFields(alice), WithoutLocalizedFields(bob));
        foreach (var room in new[] { alice, bob })
        {
            AssertAbsent(room.ToJsonString(), AliceFirstOption, AliceFirstLabel, AliceFirstLabelPt, BobFirstOption, BobFirstLabel, BobFirstLabelPt);
        }
    }

    [Theory]
    [InlineData(ConstellationAnswered, "actions")]
    [InlineData(RoutesClues, "routes-actions")]
    public async Task RoomErrorsCarryOnlyTheProblemFieldsAndNoPrivateData(string stage, string route)
    {
        await using var scene = await SceneAsync(stage);
        var canaries = scene.PartnerCanaries(viewerIsAlice: false).ToArray();
        var current = await ReadRoomAsync(scene.Bob, scene.RoomPath);
        var stale = (long.Parse((string)current[VersionKey]!, CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture);
        var endpoint = $"{scene.RoomPath}/{route}";

        using var invalid = await scene.Bob.PostJsonAsync(endpoint, ActionBody(stage, "not-a-version"));
        await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED", canaries);

        using var conflict = await scene.Bob.PostJsonAsync(endpoint, ActionBody(stage, stale));
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "VERSION_CONFLICT", canaries);

        using var foreign = await scene.Third.GetAsync(scene.RoomPath);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        AssertAbsent(FullText(foreign, await foreign.Content.ReadAsStringAsync(Ct)), canaries);
    }

    private static object ActionBody(string stage, string version) => stage == ConstellationAnswered
        ? new RoomActionRequest(Guid.NewGuid(), version, ChooseOption, FirstCard, BobFirstOption)
        : new RoutesActionRequest(Guid.NewGuid(), version, Acknowledge, "Clues", null, null);

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, string[] canaries)
    {
        var problem = await ProblemAssert.IsProblemAsync(response, status, code);
        Assert.Equal(Sorted(Problem), Sorted(KeysOf(problem)));
        AssertAbsent(FullText(response, problem.ToJsonString()), canaries);
    }

    private static string[] ExpectedKeys(string stage, bool viewerIsAlice) => stage switch
    {
        ConstellationInvited or RoutesInvited => Room,
        ConstellationAnswered => [.. Room, .. ConstellationGame, .. OpenCard, .. (viewerIsAlice ? OwnAnswer : None)],
        ConstellationRevealed => [.. Room, .. ConstellationGame, .. OpenCard, .. RevealedCard],
        ConstellationPassed or ConstellationPassedByBob => [.. Room, .. ConstellationGame, .. OpenCard, .. PassedCard],
        ConstellationCompleted => [.. Room, .. ConstellationGame, .. RevealedCard],
        RoutesIntroduction or RoutesClues => [.. Room, .. Audio, .. RoutesGame],
        RoutesChoosing => [.. Room, .. Audio, .. RoutesGame, .. OpenStop, .. ChosenStop],
        RoutesCompleted => [.. Room, .. Audio, .. RoutesGame, .. ChosenStop],
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Estado sem lista de chaves."),
    };

    private static string[] Sorted(IEnumerable<string> keys) => [.. keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static IEnumerable<string> KeysOf(JsonNode? node, string path = "")
    {
        switch (node)
        {
            case JsonObject item:
                return item.SelectMany(property =>
                {
                    var key = path.Length == 0 ? property.Key : $"{path}.{property.Key}";
                    return KeysOf(property.Value, key).Prepend(key);
                });
            case JsonArray list:
                return list.SelectMany(element => KeysOf(element, $"{path}[]"));
            default:
                return [];
        }
    }

    /// <summary>Corpo e cabeçalhos: o canário não pode sair por nenhum dos dois.</summary>
    private static string FullText(HttpResponseMessage response, string body) => $"{response.Headers}\n{response.Content.Headers}\n{body}";

    private static void AssertAbsent(string text, params IEnumerable<string> canaries)
    {
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>As opções do cartão aberto são públicas para os dois; fora delas, a resposta alheia não pode aparecer.</summary>
    private static string WithoutOpenCard(JsonObject room)
    {
        var copy = room.DeepClone().AsObject();
        copy[GameKey]![CurrentCardKey] = null;
        return copy.ToJsonString();
    }

    private static string PassedCardJson(string prompt) => $$"""[{"cardId":"{{FirstCard}}","prompt":"{{prompt}}"}]""";

    /// <summary>
    /// Os campos que <see cref="WithoutLocalizedFields"/> deixa de fora, conferidos contra o texto publicado: sem isso,
    /// qualquer diferença entre as duas pessoas escondida neles passaria pela comparação.
    /// </summary>
    private static string LocalizedFields(JsonObject room)
    {
        var game = room[GameKey]!;
        var card = game[CurrentCardKey]!;
        return LocalizedText((string)room["displayLocale"]!, (string)game["title"]!, (string)game["objective"]!,
            (string)card[PromptKey]!, [.. card["options"]!.AsArray().Select(option => (string)option!["label"]!)]);
    }

    private static string LocalizedText(string locale, string title, string objective, string prompt, params string[] labels) =>
        new JsonObject
        {
            ["displayLocale"] = locale,
            ["title"] = title,
            ["objective"] = objective,
            [PromptKey] = prompt,
            ["labels"] = new JsonArray([.. labels.Select(label => JsonValue.Create(label))]),
        }.ToJsonString();

    /// <summary>
    /// A sala inteira, para comparar por valor as duas pessoas. As únicas exceções são os campos que mudam porque cada
    /// uma lê no próprio idioma: <c>displayLocale</c>, <c>title</c>, <c>objective</c>, os <c>prompt</c> e os <c>label</c>,
    /// conferidos à parte por <see cref="LocalizedFields"/>.
    /// </summary>
    private static string WithoutLocalizedFields(JsonObject room)
    {
        var copy = room.DeepClone().AsObject();
        copy.Remove("displayLocale");
        var game = copy[GameKey]!.AsObject();
        game.Remove("title");
        game.Remove("objective");
        var card = game[CurrentCardKey]!.AsObject();
        card.Remove(PromptKey);
        foreach (var option in card["options"]!.AsArray())
        {
            option!.AsObject().Remove("label");
        }

        foreach (var passed in game[PassedKey]!.AsArray())
        {
            passed!.AsObject().Remove(PromptKey);
        }

        return copy.ToJsonString();
    }

    private static string WithoutVersions(JsonObject room)
    {
        var copy = room.DeepClone().AsObject();
        copy.Remove(VersionKey);
        copy[GameKey]!.AsObject().Remove(ResourceVersionKey);
        return copy.ToJsonString();
    }

    private static async Task<JsonObject> ReadRoomAsync(Browser browser, string path)
    {
        using var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
    }

    private async Task<Scene> SceneAsync(string stage)
    {
        var routes = stage.StartsWith("routes", StringComparison.Ordinal);
        var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
        var source = NpgsqlDataSource.Create(connectionString);
        var clock = new FakeTimeProvider(now);
        var service = new ParticipationService(source, clock);
        var (eventId, round) = routes
            ? await RoutesEventAsync(connectionString, source, service, now)
            : (seed.Events[0], seed.Rounds[0]);
        await using (var connection = await source.OpenConnectionAsync(Ct))
        {
            await Execute(connection, "update identity.users set preferred_locale='pt-BR' where id=$1", Ct, seed.Bob);
            if (!routes)
            {
                await UseTwoCardContentAsync(connection, eventId, now);
            }
        }
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, false, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RegisterAsync(eventId, user, true, Ct));
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.BuildRoundPairingsAsync(round, Ct);
        var room = (await service.StatusAsync(eventId, seed.Alice, Ct))!.RoomId!.Value;
        var factory = new DuoraWebFactory(connectionString) { Time = clock };
        var identity = new IdentityScenario(connectionString, factory);
        var alice = await identity.SignedInBrowserAsync(new(seed.Alice, "alice@example.com", IdentityScenario.ValidPassword));
        var bob = await identity.SignedInBrowserAsync(new(seed.Bob, "bob@example.com", IdentityScenario.ValidPassword));
        var third = await identity.SignedInBrowserAsync(new(seed.Third, "third@example.com", IdentityScenario.ValidPassword));
        await using (var connection = await source.OpenConnectionAsync(Ct))
        await using (var query = Command(connection, "select pairing_id from experiences.game_sessions where id=$1", room))
        {
            var scene = new Scene(source, factory, service, seed, room, (Guid)(await query.ExecuteScalarAsync(Ct))!, alice, bob, third);
            await scene.AdvanceToAsync(stage);
            return scene;
        }
    }

    private static async Task<(Guid EventId, Guid Round)> RoutesEventAsync(
        string connectionString, NpgsqlDataSource source, ParticipationService service, DateTimeOffset now)
    {
        var eventId = await RoutesBrowserScenarioSeed.InitializeAsync(connectionString, service, now, Ct);
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var query = Command(connection, "select id from events.rounds where event_id=$1", eventId);
        return (eventId, (Guid)(await query.ExecuteScalarAsync(Ct))!);
    }

    /// <summary>A seed tem um cartão só; com dois, existe sala ativa depois da primeira revelação.</summary>
    private static async Task UseTwoCardContentAsync(NpgsqlConnection connection, Guid eventId, DateTimeOffset now)
    {
        var en = new ConstellationContent(2, "Shared choices", "Explore choices together.",
        [
            new(FirstCard, FirstPrompt, [new(AliceFirstOption, AliceFirstLabel), new(BobFirstOption, BobFirstLabel)]),
            new(SecondCard, "Last light?", [new("cedar-gamma", "Cedar Gamma"), new("harbor-delta", "Harbor Delta")]),
        ]);
        var pt = new ConstellationContent(2, "Escolhas em comum", "Explore escolhas em dupla.",
        [
            new(FirstCard, FirstPromptPt, [new(AliceFirstOption, AliceFirstLabelPt), new(BobFirstOption, BobFirstLabelPt)]),
            new(SecondCard, "Última luz?", [new("cedar-gamma", "Cedro Gama"), new("harbor-delta", "Porto Delta")]),
        ]);
        var contentId = Guid.NewGuid();
        await Execute(connection, """
            insert into experiences.experience_versions(id,content_version,rules_version,schema_version,editorial_state,content_en,content_pt,published_at)
            values($1,2,1,1,'Published',$2::jsonb,$3::jsonb,$4)
            """, Ct, contentId, JsonSerializer.Serialize(en, JsonSerializerOptions.Web), JsonSerializer.Serialize(pt, JsonSerializerOptions.Web), now);
        await Execute(connection, "update events.events set experience_version_id=$2 where id=$1", Ct, eventId, contentId);
    }

    private sealed class Scene(NpgsqlDataSource source, DuoraWebFactory factory, ParticipationService service, ParticipationSeed seed,
        Guid room, Guid pairing, Browser alice, Browser bob, Browser third) : IAsyncDisposable
    {
        private readonly PublishRoutesCatalogRequest _routes = RoutesCatalogPublicationTests.Request();
        private int? _aliceClueSet;

        public ParticipationSeed Seed { get; } = seed;

        public Browser Alice { get; } = alice;

        public Browser Bob { get; } = bob;

        public Browser Third { get; } = third;

        public Guid AliceAction { get; } = Guid.NewGuid();

        private Guid BobAction { get; } = Guid.NewGuid();

        public string RoomPath => $"/api/v1/rooms/{room}";

        public async Task AcceptBothAsync()
        {
            Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, Seed.Alice, true, Ct));
            Assert.Equal(ParticipationOutcome.Done, await service.RespondAsync(room, Seed.Bob, true, Ct));
        }

        public async Task ChooseAsync(Guid user, Guid action, string card, string option)
        {
            var current = (await service.RoomAsync(room, user, Ct))!;
            Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, user, action, current.Version, ChooseOption, card, option, Ct));
        }

        public async Task AdvanceToAsync(string stage)
        {
            if (stage is ConstellationInvited or RoutesInvited)
            {
                return;
            }

            await AcceptBothAsync();
            if (stage.StartsWith("constellation", StringComparison.Ordinal))
            {
                await PlayConstellationAsync(stage);
            }
            else if (stage != RoutesIntroduction)
            {
                await PlayRoutesAsync(stage);
            }
        }

        /// <summary>Tudo o que não pode chegar a quem está lendo: identidade do par, ação dele e pistas dele.</summary>
        public IEnumerable<string> PartnerCanaries(bool viewerIsAlice)
        {
            var partner = viewerIsAlice ? Seed.Bob : Seed.Alice;
            string[] identity =
            [
                partner.ToString(), partner.ToString("N"), viewerIsAlice ? "bob@example.com" : "alice@example.com",
                pairing.ToString(), viewerIsAlice ? BobAction.ToString() : AliceAction.ToString(),
            ];
            return identity.Concat(PartnerClues(viewerIsAlice));
        }

        private IEnumerable<string> PartnerClues(bool viewerIsAlice)
        {
            int[] sets = _aliceClueSet is not { } aliceSet ? [0, 1] : [viewerIsAlice ? 1 - aliceSet : aliceSet];
            return sets.SelectMany(set => _routes.EnUS.ClueSets[set].Concat(_routes.PtBR.ClueSets[set])).Select(clue => clue.Text);
        }

        private async Task PlayConstellationAsync(string stage)
        {
            if (stage is ConstellationPassed or ConstellationPassedByBob)
            {
                var (skipper, skipAction, chooser, chooseAction, option) = stage == ConstellationPassed
                    ? (Seed.Alice, AliceAction, Seed.Bob, BobAction, BobFirstOption)
                    : (Seed.Bob, BobAction, Seed.Alice, AliceAction, AliceFirstOption);
                var current = (await service.RoomAsync(room, skipper, Ct))!;
                Assert.Equal(ParticipationOutcome.Done, await service.ActionAsync(room, skipper, skipAction, current.Version, SkipCard, FirstCard, null, Ct));
                await ChooseAsync(chooser, chooseAction, FirstCard, option);
                return;
            }

            await ChooseAsync(Seed.Alice, AliceAction, FirstCard, AliceFirstOption);
            if (stage == ConstellationAnswered)
            {
                return;
            }

            await ChooseAsync(Seed.Bob, BobAction, FirstCard, BobFirstOption);
            if (stage == ConstellationCompleted)
            {
                await ChooseAsync(Seed.Alice, Guid.NewGuid(), SecondCard, "cedar-gamma");
                await ChooseAsync(Seed.Bob, Guid.NewGuid(), SecondCard, "harbor-delta");
            }
        }

        private async Task PlayRoutesAsync(string stage)
        {
            await BothAsync(Acknowledge, "Introduction");
            _aliceClueSet = await PersistedClueSetAsync(Seed.Alice);
            if (stage == RoutesClues)
            {
                return;
            }

            await BothAsync(Acknowledge, "Clues");
            var chooser = (await service.RoomAsync(room, Seed.Alice, Ct))!.RoutesGame!.IsMyTurn ? Seed.Alice : Seed.Bob;
            await RouteActionAsync(chooser, ChooseOption, null, FirstStop, "park");
            if (stage == RoutesCompleted)
            {
                await RouteActionAsync(chooser == Seed.Alice ? Seed.Bob : Seed.Alice, ChooseOption, null, SecondStop, "lake");
                await BothAsync(ConfirmRoute, null);
            }
        }

        /// <summary>Lido do banco, não da projeção: a projeção é o que está sob teste e não pode definir o canário.</summary>
        private async Task<int> PersistedClueSetAsync(Guid user)
        {
            await using var connection = await source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select (private_state->>'clueSetIndex')::int from experiences.game_participants where game_session_id=$1 and user_id=$2", room, user);
            return (int)(await query.ExecuteScalarAsync(Ct))!;
        }

        private async Task BothAsync(string type, string? routeStage)
        {
            await RouteActionAsync(Seed.Alice, type, routeStage, null, null);
            await RouteActionAsync(Seed.Bob, type, routeStage, null, null);
        }

        private async Task RouteActionAsync(Guid user, string type, string? routeStage, string? step, string? option)
        {
            var current = (await service.RoomAsync(room, user, Ct))!;
            var outcome = await service.RoutesActionAsync(room, user, Guid.NewGuid(), current.Version, type, routeStage, step, option, Ct);
            Assert.Equal(ParticipationOutcome.Done, outcome);
        }

        public async ValueTask DisposeAsync()
        {
            Alice.Dispose();
            Bob.Dispose();
            Third.Dispose();
            await factory.DisposeAsync();
            await source.DisposeAsync();
        }
    }
}
