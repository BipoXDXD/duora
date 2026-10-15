using Duora.Core.Experiences;
using Duora.Core.Experiences.Constellation;

namespace Duora.UnitTests.Experiences.Constellation;

/// <summary>
/// Conteúdo de exemplo em PT-BR e atalhos para montar sessões de Constelação de Escolhas nos testes. Cartões leves:
/// nada íntimo, nenhum trauma, nenhuma pergunta que permita inferir atributo sensível (spec 6.3). O conteúdo vive
/// só nos testes; o de produção vem do catálogo aprovado (ADR-0013). Todo rótulo é único no conteúdo inteiro, para
/// que procurar um rótulo no JSON aponte uma única escolha.
/// </summary>
internal static class ConstellationFixture
{
    public static readonly DateTimeOffset RoundStart = new(2026, 10, 16, 23, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Deadline = RoundStart.AddMinutes(8);

    public static readonly ParticipantId Ana = new(Guid.Parse("0199d700-0000-7000-8000-00000000000a"));
    public static readonly ParticipantId Bia = new(Guid.Parse("0199d700-0000-7000-8000-00000000000b"));
    public static readonly ParticipantId Outsider = new(Guid.Parse("0199d700-0000-7000-8000-0000000000ff"));

    public const string SundayCard = "lazy-sunday";
    public const string PowerCard = "useless-power";
    public const string IceCreamCard = "ice-cream";

    public const string Picnic = "Piquenique no parque";
    public const string Movies = "Maratona de filmes";
    public const string StreetFair = "Feira de rua";
    public const string NewRecipe = "Cozinhar uma receita nova";
    public const string ParkingSpot = "Sempre achar vaga";
    public const string PerfectToast = "Torrada sempre no ponto";
    public const string SongLyrics = "Lembrar toda letra de música";
    public const string PopcornCaramel = "Pipoca com caramelo";
    public const string CornCake = "Bolo de fubá";
    public const string CinnamonCoffee = "Café com canela";

    public static ConstellationContent ContentV1() => Content(contentVersion: 1, title: "Constelação de domingo");

    public static ConstellationContent Content(int contentVersion, string title) => new(
        contentVersion,
        title,
        objective: "Responder a cartões leves e descobrir o que vocês têm em comum, sem precisar se expor.",
        cards:
        [
            new ConstellationCard(SundayCard, "Para um domingo sem planos, qual destas?",
            [
                new ConstellationOption("picnic", Picnic),
                new ConstellationOption("movies", Movies),
                new ConstellationOption("street-fair", StreetFair),
                new ConstellationOption("new-recipe", NewRecipe),
            ]),
            new ConstellationCard(PowerCard, "Que superpoder inútil você escolheria?",
            [
                new ConstellationOption("parking-spot", ParkingSpot),
                new ConstellationOption("perfect-toast", PerfectToast),
                new ConstellationOption("song-lyrics", SongLyrics),
            ]),
            new ConstellationCard(IceCreamCard, "Que sabor de sorvete inventado você provaria primeiro?",
            [
                new ConstellationOption("popcorn-caramel", PopcornCaramel),
                new ConstellationOption("corn-cake", CornCake),
                new ConstellationOption("cinnamon-coffee", CinnamonCoffee),
            ]),
        ]);

    public static ConstellationEngine Engine(TimeProvider clock) => new(new ConstellationRules(), clock);

    public static ManualTimeProvider ClockAtRoundStart() => new(RoundStart);

    public static ConstellationSession NewSession(ConstellationEngine engine, ConstellationContent? content = null) =>
        engine.CreateSession(
            content ?? ContentV1(), new ParticipantPair(Ana, Bia), RoundStart, GameTimeLimits.Default, new SessionSeed(7));

    public static ConstellationAction Choose(string cardId, string optionId) =>
        new ConstellationAction.ChooseOption(cardId, optionId);

    public static ConstellationAction Skip(string cardId) => new ConstellationAction.SkipCard(cardId);

    public static GameCommand<ConstellationAction> Command(
        ConstellationSession session, ParticipantId actor, ConstellationAction action, ActionId? actionId = null) =>
        new(actionId ?? new ActionId(Guid.NewGuid()), actor, session.Version, action);

    /// <summary>Valida e aplica; falha o teste se a ação não for aceita.</summary>
    public static ConstellationSession Act(
        ConstellationEngine engine, ConstellationSession session, ParticipantId actor, ConstellationAction action) =>
        Apply(engine, session, Command(session, actor, action));

    public static ConstellationSession Apply(
        ConstellationEngine engine, ConstellationSession session, GameCommand<ConstellationAction> command)
    {
        var validation = engine.ValidateAction(session, command);
        var accepted = Assert.IsType<ActionValidation<ConstellationState, ConstellationAction>.Accepted>(validation);
        return engine.ApplyAction(accepted.Action).Session;
    }

    /// <summary>Primeiro cartão revelado (as duas escolhem "Feira de rua"), segundo passado (Bia pula).</summary>
    public static ConstellationSession AtThirdCard(ConstellationEngine engine, ConstellationSession session)
    {
        session = Act(engine, session, Ana, Choose(SundayCard, "street-fair"));
        session = Act(engine, session, Bia, Choose(SundayCard, "street-fair"));
        session = Act(engine, session, Ana, Choose(PowerCard, "song-lyrics"));
        return Act(engine, session, Bia, Skip(PowerCard));
    }
}
