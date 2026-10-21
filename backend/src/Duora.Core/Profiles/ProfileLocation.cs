namespace Duora.Core.Profiles;

/// <summary>Approximate location only; null country means undisclosed. Bootstrap catalog preserves existing UFs.</summary>
public sealed record ProfileLocation(string? CountryCode, string? SubdivisionCode);

/// <summary>Versioned compatibility seed, not a decision on launch countries or long-term catalog maintenance.</summary>
public static class ProfileLocationCatalog
{
    public static IReadOnlyList<string> Countries { get; } = ["BR"];

    public static IReadOnlyList<ProfileLocation> Subdivisions { get; } =
        RegionCode.All.Select(code => new ProfileLocation("BR", code)).ToArray();

    public static bool IsValid(string? country, string? subdivision) =>
        country is null ? subdivision is null
        : Countries.Contains(country, StringComparer.Ordinal)
          && (subdivision is null || Subdivisions.Contains(new ProfileLocation(country, subdivision)));
}
