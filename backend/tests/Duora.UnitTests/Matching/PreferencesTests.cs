using Duora.Core.Matching;

namespace Duora.UnitTests.Matching;

public sealed class PreferencesTests
{
    private const string Intent = "intencao";

    [Fact]
    public void UnrestrictedAcceptsSomeoneWhoDeclaredNothing()
    {
        Assert.True(Preferences.Unrestricted.Accepts(Preferences.Unrestricted));
    }

    [Theory]
    [InlineData("amizade", true)]
    [InlineData("romance", false)]
    public void AcceptsOnlyDeclaredValuesInsideTheAcceptedSet(string declared, bool expected)
    {
        var requiring = Requiring(Intent, "amizade");
        var other = new Preferences(new Dictionary<string, string> { [Intent] = declared }, AcceptsAnyone());

        Assert.Equal(expected, requiring.Accepts(other));
    }

    [Fact]
    public void DoesNotAcceptSomeoneWhoLeftARequiredCriterionUnanswered()
    {
        Assert.False(Requiring(Intent, "amizade").Accepts(Preferences.Unrestricted));
    }

    [Fact]
    public void KeepsItsOwnCopyOfTheAnswers()
    {
        var accepted = new HashSet<string> { "amizade" };
        var preferences = new Preferences(
            new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlySet<string>> { [Intent] = accepted });

        accepted.Add("romance");

        var romance = new Preferences(new Dictionary<string, string> { [Intent] = "romance" }, AcceptsAnyone());
        Assert.False(preferences.Accepts(romance));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsBlankCriterion(string criterion)
    {
        Assert.Throws<ArgumentException>(
            () => new Preferences(new Dictionary<string, string> { [criterion] = "amizade" }, AcceptsAnyone()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsBlankDeclaredValue(string value)
    {
        Assert.Throws<ArgumentException>(
            () => new Preferences(new Dictionary<string, string> { [Intent] = value }, AcceptsAnyone()));
    }

    [Fact]
    public void RejectsBlankAcceptedValue()
    {
        Assert.Throws<ArgumentException>(() => Requiring(Intent, " "));
    }

    /// <summary>Conjunto vazio seria "não aceito ninguém": ambíguo com dado faltando, então não entra na rodada.</summary>
    [Fact]
    public void RejectsEmptyAcceptedSet()
    {
        Assert.Throws<ArgumentException>(() => new Preferences(
            new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlySet<string>> { [Intent] = new HashSet<string>() }));
    }

    private static Preferences Requiring(string criterion, string value) =>
        new(
            new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlySet<string>> { [criterion] = new HashSet<string> { value } });

    private static Dictionary<string, IReadOnlySet<string>> AcceptsAnyone() => [];
}
