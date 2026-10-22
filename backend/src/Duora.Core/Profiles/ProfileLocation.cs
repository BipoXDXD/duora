namespace Duora.Core.Profiles;

/// <summary>Approximate location only; null country means undisclosed. Bootstrap catalog preserves existing UFs.</summary>
public sealed record ProfileLocation(string? CountryCode, string? SubdivisionCode);

/// <summary>Versioned compatibility seed, not a decision on launch countries or long-term catalog maintenance.</summary>
public static class ProfileLocationCatalog
{
    public static IReadOnlyList<string> Countries { get; } = ["BR", "US"];

    // CLDR 48.2 / Unicode-3.0; provenance in content/location/cldr-48.2.
    private static readonly string[] UnitedStatesSubdivisions =
    [
        "US-AK", "US-AL", "US-AR", "US-AZ", "US-CA", "US-CO", "US-CT", "US-DC",
        "US-DE", "US-FL", "US-GA", "US-HI", "US-IA", "US-ID", "US-IL", "US-IN",
        "US-KS", "US-KY", "US-LA", "US-MA", "US-MD", "US-ME", "US-MI", "US-MN",
        "US-MO", "US-MS", "US-MT", "US-NC", "US-ND", "US-NE", "US-NH", "US-NJ",
        "US-NM", "US-NV", "US-NY", "US-OH", "US-OK", "US-OR", "US-PA", "US-RI",
        "US-SC", "US-SD", "US-TN", "US-TX", "US-UT", "US-VA", "US-VT", "US-WA",
        "US-WI", "US-WV", "US-WY",
    ];

    public static IReadOnlyList<ProfileLocation> Subdivisions { get; } =
        RegionCode.All.Select(code => new ProfileLocation("BR", code))
            .Concat(UnitedStatesSubdivisions.Select(code => new ProfileLocation("US", code))).ToArray();

    public static bool IsValid(string? country, string? subdivision) =>
        country is null ? subdivision is null
        : Countries.Contains(country, StringComparer.Ordinal)
          && (subdivision is null || Subdivisions.Contains(new ProfileLocation(country, subdivision)));
}
