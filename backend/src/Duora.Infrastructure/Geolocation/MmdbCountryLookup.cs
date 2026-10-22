using System.Net;

namespace Duora.Infrastructure.Geolocation;

/// <summary>Local-only adapter, explicitly registered only when detector configuration is enabled.</summary>
public sealed class MmdbCountryLookup(ActiveCountryDatabase database, bool enabled = false) : ICountryLookup
{
    public bool Enabled => enabled;
    public CountryLookupResult Lookup(IPAddress address) => enabled ? database.Lookup(address) : new(CountryLookupStatus.Unavailable);
}
