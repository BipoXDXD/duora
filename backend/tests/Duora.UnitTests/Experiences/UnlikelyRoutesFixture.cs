using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;

namespace Duora.UnitTests.Experiences;

/// <summary>
/// Conteúdo de exemplo em PT-BR e atalhos para montar sessões de Rotas Improváveis nos testes. Cenário fictício:
/// nenhum endereço, rotina ou localização real.
/// </summary>
internal static class UnlikelyRoutesFixture
{
    public static readonly DateTimeOffset RoundStart = new(2026, 10, 16, 23, 0, 0, TimeSpan.Zero);

    public static readonly ParticipantId Ana = new(Guid.Parse("0199d700-0000-7000-8000-00000000000a"));
    public static readonly ParticipantId Bia = new(Guid.Parse("0199d700-0000-7000-8000-00000000000b"));
    public static readonly ParticipantId Outsider = new(Guid.Parse("0199d700-0000-7000-8000-0000000000ff"));

    public const string AnaClueOne = "O passeio começa perto da água.";
    public const string AnaClueTwo = "Nada depois das 18h no roteiro.";
    public const string BiaClueOne = "A última parada precisa ter vista do alto.";
    public const string BiaClueTwo = "Ninguém do grupo gosta de lugares barulhentos.";

    /// <summary>Semente em que Ana recebe o primeiro conjunto de pistas e escolhe primeiro (ver teste de semente).</summary>
    public static readonly SessionSeed AnaFirstSeed = new(1);

    public static UnlikelyRoutesContent ContentV1() => Content(contentVersion: 1, title: "Um passeio por Vila Inventada");

    public static UnlikelyRoutesContent Content(int contentVersion, string title) => new(
        contentVersion,
        title,
        objective: "Montar juntos um passeio por uma cidade inventada.",
        example: "Exemplo: se uma pista diz \"começa cedo\", escolham primeiro um lugar que abre de manhã.",
        stops:
        [
            new RouteStop("route-stop-1", "Escolha a primeira parada",
            [
                new RouteOption("lighthouse", "Farol desativado"),
                new RouteOption("floating-market", "Mercado flutuante"),
                new RouteOption("underground-library", "Biblioteca subterrânea"),
            ]),
            new RouteStop("route-stop-2", "Escolha a segunda parada",
            [
                new RouteOption("clock-garden", "Jardim de relógios"),
                new RouteOption("tram-station", "Estação de bondinhos"),
            ]),
            new RouteStop("route-stop-3", "Escolha a terceira parada",
            [
                new RouteOption("map-fair", "Feira de mapas antigos"),
                new RouteOption("violin-bridge", "Ponte dos violinos"),
            ]),
            new RouteStop("route-stop-4", "Escolha a última parada",
            [
                new RouteOption("cloud-observatory", "Observatório de nuvens"),
                new RouteOption("quiet-tower", "Torre silenciosa"),
            ]),
        ],
        clueSets:
        [
            [new Clue(AnaClueOne), new Clue(AnaClueTwo)],
            [new Clue(BiaClueOne), new Clue(BiaClueTwo)],
        ]);

    public static UnlikelyRoutesEngine Engine(TimeProvider clock, int maxRevisions = 1) =>
        new(new UnlikelyRoutesRules(new UnlikelyRoutesSettings(maxRevisions)), clock);

    public static ManualTimeProvider ClockAtRoundStart() => new(RoundStart);

    public static UnlikelyRoutesSession NewSession(
        UnlikelyRoutesEngine engine, UnlikelyRoutesContent? content = null, SessionSeed? seed = null) =>
        engine.CreateSession(
            content ?? ContentV1(),
            new ParticipantPair(Ana, Bia),
            RoundStart,
            GameTimeLimits.Default,
            seed ?? AnaFirstSeed);

    public static GameCommand<UnlikelyRoutesAction> Command(
        UnlikelyRoutesSession session, ParticipantId actor, UnlikelyRoutesAction action, ActionId? actionId = null) =>
        new(actionId ?? new ActionId(Guid.NewGuid()), actor, session.Version, action);

    /// <summary>Valida e aplica; falha o teste se a ação não for aceita.</summary>
    public static UnlikelyRoutesSession Act(
        UnlikelyRoutesEngine engine, UnlikelyRoutesSession session, ParticipantId actor, UnlikelyRoutesAction action) =>
        Apply(engine, session, Command(session, actor, action));

    public static UnlikelyRoutesSession Apply(
        UnlikelyRoutesEngine engine, UnlikelyRoutesSession session, GameCommand<UnlikelyRoutesAction> command)
    {
        var validation = engine.ValidateAction(session, command);
        var accepted = Assert.IsType<ActionValidation<UnlikelyRoutesState, UnlikelyRoutesAction>.Accepted>(validation);
        return engine.ApplyAction(accepted.Action).Session;
    }

    /// <summary>Leva a sessão até a etapa de escolhas: os dois dão "entendi" na introdução e nas pistas.</summary>
    public static UnlikelyRoutesSession AtChoices(UnlikelyRoutesEngine engine, UnlikelyRoutesSession session)
    {
        session = Act(engine, session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        session = Act(engine, session, Bia, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Introduction));
        session = Act(engine, session, Ana, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Clues));
        return Act(engine, session, Bia, new UnlikelyRoutesAction.Acknowledge(RoutesStage.Clues));
    }

    /// <summary>Faz as quatro escolhas alternadas (Ana começa com <see cref="AnaFirstSeed"/>).</summary>
    public static UnlikelyRoutesSession AtAdjustment(UnlikelyRoutesEngine engine, UnlikelyRoutesSession session)
    {
        session = AtChoices(engine, session);
        session = Act(engine, session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));
        session = Act(engine, session, Bia, new UnlikelyRoutesAction.ChooseOption("route-stop-2", "tram-station"));
        session = Act(engine, session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-3", "map-fair"));
        return Act(engine, session, Bia, new UnlikelyRoutesAction.ChooseOption("route-stop-4", "cloud-observatory"));
    }
}
