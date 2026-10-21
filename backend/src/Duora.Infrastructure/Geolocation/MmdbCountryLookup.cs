using System.Net;

namespace Duora.Infrastructure.Geolocation;

/// <summary>Local-only adapter. Not registered in Web; even explicit construction is disabled by default.</summary>
public sealed class MmdbCountryLookup(ActiveCountryDatabase database, bool enabled = false) : ICountryLookup
{
    public bool Enabled => enabled;
    public CountryLookupResult Lookup(IPAddress address) => enabled ? database.Lookup(address) : new(CountryLookupStatus.Unavailable);
}
