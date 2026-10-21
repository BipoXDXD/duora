namespace Duora.Core.Profiles;

public enum RegionalPreferenceMode
{
    Pending,
    Countries,
    Unrestricted,
}

/// <summary>Explicit private country preference, independent of location and communication languages.</summary>
public sealed class RegionalPreference
{
    private RegionalPreference(RegionalPreferenceMode mode, string[] countries)
    {
        Mode = mode;
        CountryCodes = Array.AsReadOnly(countries);
    }

    public RegionalPreferenceMode Mode { get; }
    public IReadOnlyList<string> CountryCodes { get; }
    public static RegionalPreference Pending { get; } = new(RegionalPreferenceMode.Pending, []);

    public static bool TryCreate(string? mode, string[]? countryCodes, out RegionalPreference? preference)
    {
        preference = null;
        if (countryCodes is null || mode is not ("countries" or "unrestricted")
            || (mode == "countries" ? countryCodes.Length == 0 : countryCodes.Length != 0)
            || countryCodes.Any(code => !ProfileLocationCatalog.Countries.Contains(code, StringComparer.Ordinal))
            || countryCodes.Distinct(StringComparer.Ordinal).Count() != countryCodes.Length)
        {
            return false;
        }

        preference = new RegionalPreference(mode == "countries" ? RegionalPreferenceMode.Countries : RegionalPreferenceMode.Unrestricted,
            countryCodes.Order(StringComparer.Ordinal).ToArray());
        return true;
    }
}
