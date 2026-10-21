using Duora.Core.Profiles;

namespace Duora.UnitTests.Profiles;

public sealed class RegionalPreferenceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pending")]
    [InlineData("COUNTRIES")]
    public void RequiresAnExplicitSupportedMode(string? mode) =>
        Assert.False(RegionalPreference.TryCreate(mode, [], out _));

    [Fact]
    public void MissingCountryListIsNotUnrestricted() =>
        Assert.False(RegionalPreference.TryCreate("unrestricted", null, out _));

    [Theory]
    [InlineData("countries", new string[0])]
    [InlineData("countries", new[] { "US" })]
    [InlineData("countries", new[] { "BR", "BR" })]
    [InlineData("countries", new[] { "br" })]
    [InlineData("countries", new[] { "BR-SP" })]
    [InlineData("countries", new[] { "" })]
    [InlineData("countries", new string[] { null! })]
    [InlineData("unrestricted", new[] { "BR" })]
    public void RejectsIncoherentOrUnsupportedCountries(string mode, string[] countries) =>
        Assert.False(RegionalPreference.TryCreate(mode, countries, out _));

    [Fact]
    public void AcceptedCountriesAreCopiedAndCannotBeChangedByTheCaller()
    {
        string[] countries = ["BR"];
        Assert.True(RegionalPreference.TryCreate("countries", countries, out var preference));
        countries[0] = "US";
        Assert.Equal(RegionalPreferenceMode.Countries, preference!.Mode);
        Assert.Equal(["BR"], preference.CountryCodes);
    }

    [Fact]
    public void UnrestrictedRequiresAnExplicitEmptyList()
    {
        Assert.True(RegionalPreference.TryCreate("unrestricted", [], out var preference));
        Assert.Equal(RegionalPreferenceMode.Unrestricted, preference!.Mode);
        Assert.Empty(preference.CountryCodes);
    }

    [Fact]
    public void PendingDoesNotBecomeAnUnrestrictedDeclaration()
    {
        Assert.Equal(RegionalPreferenceMode.Pending, RegionalPreference.Pending.Mode);
        Assert.Empty(RegionalPreference.Pending.CountryCodes);
    }
}
