using System.Collections.Concurrent;
using System.Globalization;

namespace Duora.Infrastructure.Geolocation;

/// <summary>Logical contract only. The actual MMDB type and operational budgets still require G2 approval.</summary>
internal static class IpinfoLiteSchema
{
    internal const string Product = "ipinfo_lite.mmdb";
    internal const string Schema = "ipinfo-lite-country-v1";
    internal const string License = "CC-BY-SA-4.0";
    internal const string Source = "https://ipinfo.io/data/ipinfo_lite.mmdb";
    private static readonly ConcurrentDictionary<string, bool> CountryCodes = new(StringComparer.Ordinal);

    internal static bool IsCountryCode(string? code)
    {
        if (code is not { Length: 2 } || !char.IsAsciiLetterUpper(code[0]) || !char.IsAsciiLetterUpper(code[1]))
        { return false; }
        // At most 26 * 26 entries; do not allocate RegionInfo for every network in the database.
        return CountryCodes.GetOrAdd(code, static value =>
        {
            try
            {
                return string.Equals(new RegionInfo(value).TwoLetterISORegionName, value, StringComparison.Ordinal);
            }
            catch (ArgumentException) { return false; }
        });
    }
}
