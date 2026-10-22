using System.Net;

namespace Duora.Infrastructure.Geolocation;

// Country lookup stays local. Runtime registration is opt-in and never writes participant attributes.
public interface ICountryLookup
{
    bool Enabled { get; }
    CountryLookupResult Lookup(IPAddress address);
}

public enum CountryLookupStatus { Detected, Unknown, Unavailable }

public sealed record CountryLookupResult(CountryLookupStatus Status, string? CountryCode = null);

public sealed class DisabledCountryLookup : ICountryLookup
{
    public bool Enabled => false;
    public CountryLookupResult Lookup(IPAddress address) => new(CountryLookupStatus.Unavailable);
}
