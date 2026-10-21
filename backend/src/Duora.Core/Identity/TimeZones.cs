namespace Duora.Core.Identity;

/// <summary>IANA identifiers supported by this runtime; a saved preference is never a fixed UTC offset.</summary>
public static class TimeZones
{
    private static readonly HashSet<string> Supported = TimeZoneInfo.GetSystemTimeZones()
        .Where(zone => zone.HasIanaId).Select(zone => zone.Id).Append("UTC").ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> Identifiers { get; } = Array.AsReadOnly(Supported.Order(StringComparer.Ordinal).ToArray());

    public static bool IsSupported(string identifier) => Supported.Contains(identifier);
}
