using Duora.Core.Safety;

namespace Duora.UnitTests.Safety;

/// <summary>Pedido de criação de um run (ADR-0049, D1 e D4): a fronteira converte o corpo num pedido válido ou recusa.</summary>
public sealed class ReconciliationRunOrderTests
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Justification = "Revisão da prévia aprovada.";
    private const string Reference = "AUTH-321-1";

    private static readonly ReconciliationRunUnitRef Unit = new(Guid.NewGuid(), Guid.NewGuid(), Digest);

    private static ReconciliationRunUnitRef NewUnit() => new(Guid.NewGuid(), Guid.NewGuid(), Digest);

    private static string ParameterOf(Action action) => Assert.ThrowsAny<ArgumentException>(action).ParamName!;

    [Fact]
    public void AValidOrderKeepsItsFields()
    {
        var order = ReconciliationRunOrder.Create([Unit], Justification, Reference);

        Assert.Equal([Unit], order.Units);
        Assert.Equal((Justification, Reference), (order.Justification, order.AuthorizationReference));
    }

    [Fact]
    public void TheUnitCountHasAnInclusiveCeiling()
    {
        var atLimit = Enumerable.Range(0, ReconciliationRunOrder.MaxUnits).Select(_ => NewUnit()).ToArray();

        Assert.Equal(ReconciliationRunOrder.MaxUnits, ReconciliationRunOrder.Create(atLimit, Justification, Reference).Units.Count);
        Assert.Equal("units", ParameterOf(() => ReconciliationRunOrder.Create([.. atLimit, NewUnit()], Justification, Reference)));
    }

    [Fact]
    public void AnOrderWithoutUnitsIsRefused()
    {
        Assert.Equal("units", ParameterOf(() => ReconciliationRunOrder.Create([], Justification, Reference)));
        Assert.Equal("units", ParameterOf(() => ReconciliationRunOrder.Create(null, Justification, Reference)));
    }

    [Fact]
    public void TheSamePairTwiceIsRefusedEvenWithAnotherDigest()
    {
        var other = Unit with { UnitDigest = new string('b', ReconciliationUnitDigest.Length) };

        Assert.Equal("units", ParameterOf(() => ReconciliationRunOrder.Create([Unit, other], Justification, Reference)));
    }

    [Fact]
    public void TheSameSubjectWithAnotherMeasureIsAnotherUnit()
    {
        var order = ReconciliationRunOrder.Create([Unit, Unit with { OriginalMeasureId = Guid.NewGuid() }], Justification, Reference);

        Assert.Equal(2, order.Units.Count);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("measure")]
    [InlineData("digest")]
    public void AnEmptyIdOrAMalformedDigestIsRefused(string field)
    {
        var unit = field switch
        {
            "subject" => Unit with { SubjectUserId = Guid.Empty },
            "measure" => Unit with { OriginalMeasureId = Guid.Empty },
            _ => Unit with { UnitDigest = Digest.ToUpperInvariant() },
        };

        Assert.Equal("units", ParameterOf(() => ReconciliationRunOrder.Create([unit], Justification, Reference)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("com\nquebra")]
    [InlineData("com\ttab")]
    [InlineData("com\u0000nul")]
    [InlineData("com\u0085nel")]
    public void AnInvalidTextIsRefusedInEitherField(string? text)
    {
        Assert.Equal("justification", ParameterOf(() => ReconciliationRunOrder.Create([Unit], text, Reference)));
        Assert.Equal("authorizationReference", ParameterOf(() => ReconciliationRunOrder.Create([Unit], Justification, text)));
    }

    /// <summary>Fora de atributo: nos metadados um surrogate sozinho vira U+FFFD e o caso deixaria de existir.</summary>
    [Fact]
    public void ALoneSurrogateIsRefused()
    {
        var text = new string('\ud800', 1);

        Assert.Equal("justification", ParameterOf(() => ReconciliationRunOrder.Create([Unit], text, Reference)));
    }

    [Fact]
    public void TheTextsHaveInclusiveCeilingsInUnicodeCharacters()
    {
        var justification = string.Concat(Enumerable.Repeat("😀", ReconciliationRunOrder.JustificationMaxLength));
        var reference = new string('r', ReconciliationRunOrder.AuthorizationReferenceMaxLength);

        Assert.NotNull(ReconciliationRunOrder.Create([Unit], justification, reference));
        Assert.Equal("justification", ParameterOf(() => ReconciliationRunOrder.Create([Unit], justification + "x", Reference)));
        Assert.Equal("authorizationReference", ParameterOf(() => ReconciliationRunOrder.Create([Unit], Justification, reference + "x")));
    }

    [Fact]
    public void TheIntentIgnoresTheOrderOfTheUnits()
    {
        var other = NewUnit();

        Assert.Equal(
            ReconciliationRunOrder.Create([Unit, other], Justification, Reference).Intent(),
            ReconciliationRunOrder.Create([other, Unit], Justification, Reference).Intent());
    }

    [Fact]
    public void EveryFieldChangesTheIntent()
    {
        var intent = ReconciliationRunOrder.Create([Unit], Justification, Reference).Intent();

        Assert.Equal(32, intent.Length);
        Assert.NotEqual(intent, ReconciliationRunOrder.Create([Unit], Justification + ".", Reference).Intent());
        Assert.NotEqual(intent, ReconciliationRunOrder.Create([Unit], Justification, Reference + "x").Intent());
        Assert.NotEqual(intent, ReconciliationRunOrder.Create([Unit with { UnitDigest = new string('b', 64) }], Justification, Reference).Intent());
        Assert.NotEqual(intent, ReconciliationRunOrder.Create([Unit with { SubjectUserId = Guid.NewGuid() }], Justification, Reference).Intent());
        Assert.NotEqual(intent, ReconciliationRunOrder.Create([Unit with { OriginalMeasureId = Guid.NewGuid() }], Justification, Reference).Intent());
    }

    [Fact]
    public void TheBoundaryBetweenTheTextsCountsForTheIntent() =>
        Assert.NotEqual(
            ReconciliationRunOrder.Create([Unit], "ab", "c").Intent(),
            ReconciliationRunOrder.Create([Unit], "a", "bc").Intent());
}
