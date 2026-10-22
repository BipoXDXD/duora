using Duora.Core.Profiles;

namespace Duora.UnitTests.Profiles;

public sealed class UnitedStatesLocationTests
{
    [Theory]
    [InlineData("US", null, true)]
    [InlineData("US", "US-MA", true)]
    [InlineData("US", "US-DC", true)]
    [InlineData("US", "BR-SP", false)]
    [InlineData("BR", "US-MA", false)]
    [InlineData("US", "US-PR", false)]
    [InlineData("PR", null, false)]
    [InlineData(null, "US-MA", false)]
    public void OnlyApprovedCountryAndSubdivisionPairsAreAccepted(string? country, string? subdivision, bool expected) =>
        Assert.Equal(expected, ProfileLocationCatalog.IsValid(country, subdivision));

    [Fact]
    public void CatalogPreservesBrazilAndAddsExactlyFiftyStatesAndDc()
    {
        Assert.Equal(["BR", "US"], ProfileLocationCatalog.Countries);
        Assert.Equal(27, ProfileLocationCatalog.Subdivisions.Count(x => x.CountryCode == "BR"));
        Assert.Equal(51, ProfileLocationCatalog.Subdivisions.Count(x => x.CountryCode == "US"));
        Assert.Equal(78, ProfileLocationCatalog.Subdivisions.Distinct().Count());
        Assert.False(RegionCode.TryParse("US-MA", out _));
        Assert.True(RegionalPreference.TryCreate("countries", ["BR", "US"], out var preference));
        Assert.Equal(["BR", "US"], preference!.CountryCodes);
    }
}
