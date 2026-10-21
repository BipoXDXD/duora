using Duora.Core.Geolocation;

namespace Duora.Infrastructure.Geolocation;

/// <summary>Operational metadata only: no query IP, per-person result, credential or signed URL.</summary>
public sealed record CountryDatabaseManifest(
    string Sha256, long SizeBytes, DateTimeOffset BuiltAt, DateTimeOffset FirstDownloadedAt,
    DateTimeOffset ValidatedAt, string DatabaseType,
    string Product = "country.mmdb", string Schema = "ipinfo-country-v1", string License = "CC-BY-SA-4.0",
    string Source = "https://ipinfo.io/data/free/country.mmdb")
{
    public bool IsAdmissible(DateTimeOffset now) =>
        HasCoherentMetadata(now) && DatabaseAgePolicy.Evaluate(BuiltAt, now).CanSuggest;

    internal bool HasCoherentMetadata(DateTimeOffset now) =>
        Sha256 is { Length: 64 } && Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        && SizeBytes > 0 && Product == "country.mmdb" && Schema == "ipinfo-country-v1"
        && License == "CC-BY-SA-4.0" && Source == "https://ipinfo.io/data/free/country.mmdb"
        && DatabaseType is { Length: > 0 and <= 128 }
        && DatabaseType.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')
        && BuiltAt <= FirstDownloadedAt && FirstDownloadedAt <= ValidatedAt && ValidatedAt <= now;
}

public sealed record CountryDatabaseSnapshot(long Generation, CountryDatabaseManifest Manifest);
