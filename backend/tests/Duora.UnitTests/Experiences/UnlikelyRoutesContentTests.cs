using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;

namespace Duora.UnitTests.Experiences;

/// <summary>O conteúdo do catálogo nasce válido: limites de tamanho, ids únicos e dois conjuntos de pistas.</summary>
public sealed class UnlikelyRoutesContentTests
{
    private static readonly RouteOption[] TwoOptions = [new("a", "Opção A"), new("b", "Opção B")];
    private static readonly RouteStop OneStop = new("stop-1", "Escolha", TwoOptions);
    private static readonly Clue[][] TwoClueSets = [[new Clue("Pista 1")], [new Clue("Pista 2")]];

    [Fact]
    public void ExampleContentIsValid()
    {
        var content = ContentV1();

        Assert.Equal(4, content.Stops.Count);
        Assert.Equal(2, content.ClueSets.Count);
    }

    [Fact]
    public void SmallestValidContentHasOneStopTwoOptionsAndOneCluePerSet()
    {
        var content = Build(stops: [OneStop]);

        Assert.Single(content.Stops);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ContentVersionMustBePositive(int contentVersion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(contentVersion: contentVersion));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TitleIsRequired(string title)
    {
        Assert.Throws<ArgumentException>(() => Build(title: title));
    }

    [Fact]
    public void TextAtTheLengthLimitIsAcceptedAndOneMoreIsRejected()
    {
        Build(title: new string('a', UnlikelyRoutesContent.MaxTextLength));

        Assert.Throws<ArgumentException>(() => Build(title: new string('a', UnlikelyRoutesContent.MaxTextLength + 1)));
    }

    [Fact]
    public void TextWithControlCharactersIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Build(title: "Título\u0000"));
    }

    [Fact]
    public void ContentNeedsAtLeastOneStop()
    {
        Assert.Throws<ArgumentException>(() => Build(stops: []));
    }

    [Fact]
    public void ContentAcceptsTheMaximumNumberOfStopsAndRejectsOneMore()
    {
        Build(stops: Stops(UnlikelyRoutesContent.MaxStops));

        Assert.Throws<ArgumentException>(() => Build(stops: Stops(UnlikelyRoutesContent.MaxStops + 1)));
    }

    [Fact]
    public void StopNeedsAtLeastTwoOptions()
    {
        Assert.Throws<ArgumentException>(() => new RouteStop("stop-1", "Escolha", [new RouteOption("a", "Opção A")]));
    }

    [Fact]
    public void StopAcceptsTheMaximumNumberOfOptionsAndRejectsOneMore()
    {
        var atLimit = new RouteStop("stop-1", "Escolha", Options(RouteStop.MaxOptions));

        Assert.Equal(RouteStop.MaxOptions, atLimit.Options.Count);
        Assert.Throws<ArgumentException>(() => new RouteStop("stop-1", "Escolha", Options(RouteStop.MaxOptions + 1)));
    }

    [Fact]
    public void OptionIdsMustBeUniqueWithinAStop()
    {
        Assert.Throws<ArgumentException>(
            () => new RouteStop("stop-1", "Escolha", [new RouteOption("a", "Opção A"), new RouteOption("a", "Outra")]));
    }

    [Fact]
    public void StepIdsMustBeUnique()
    {
        Assert.Throws<ArgumentException>(() => Build(stops: [OneStop, OneStop]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Com Espaço")]
    [InlineData("MAIUSCULA")]
    [InlineData("acentuação")]
    public void IdentifiersAreLowercaseSlugs(string id)
    {
        Assert.Throws<ArgumentException>(() => new RouteOption(id, "Opção"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ContentNeedsExactlyTwoClueSets(int sets)
    {
        var clueSets = Enumerable.Range(0, sets).Select(index => new[] { new Clue($"Pista {index}") }).ToArray();

        Assert.Throws<ArgumentException>(() => Build(clueSets: clueSets));
    }

    [Fact]
    public void EachClueSetNeedsAtLeastOneClue()
    {
        Assert.Throws<ArgumentException>(() => Build(clueSets: [[new Clue("Pista 1")], []]));
    }

    [Fact]
    public void ClueSetsCannotShareAClue()
    {
        Assert.Throws<ArgumentException>(() => Build(clueSets: [[new Clue("Mesma")], [new Clue("Mesma")]]));
    }

    [Fact]
    public void ContentKeepsItsOwnCopyOfTheLists()
    {
        var stops = new List<RouteStop> { OneStop };

        var content = Build(stops: stops);
        stops.Add(new RouteStop("stop-2", "Outra", TwoOptions));

        Assert.Single(content.Stops);
    }

    private static UnlikelyRoutesContent Build(
        int contentVersion = 1,
        string title = "Título",
        IReadOnlyList<RouteStop>? stops = null,
        IReadOnlyList<IReadOnlyList<Clue>>? clueSets = null) =>
        new(contentVersion, title, "Objetivo", "Exemplo", stops ?? [OneStop], clueSets ?? TwoClueSets);

    private static RouteStop[] Stops(int count) =>
        [.. Enumerable.Range(1, count).Select(index => new RouteStop($"stop-{index}", "Escolha", TwoOptions))];

    private static RouteOption[] Options(int count) =>
        [.. Enumerable.Range(1, count).Select(index => new RouteOption($"option-{index}", $"Opção {index}"))];
}
