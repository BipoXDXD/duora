using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using Duora.Core.Geolocation;
using Duora.Infrastructure.Geolocation;
using MaxMind.Db;

namespace Duora.Tools.IpinfoG2;

internal sealed record G2Evidence(
    string Mode, string Sha256, long SizeBytes, string DatabaseType,
    DateTimeOffset BuiltAt, DateTimeOffset DownloadedAt, int Networks, int Ipv4Networks, int Ipv6Networks,
    bool LiteShapeMatches, bool EpochAdmissible, bool ValidationPassed, string[] ObservedFields,
    double OpenMilliseconds, double OverlapMilliseconds, double ValidationMilliseconds,
    long AllocatedBytes, long PeakWorkingSetBytes)
{
    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    { "country_code", "country", "continent_code", "continent", "asn", "as_name", "as_domain", "network" };

    public static G2Evidence Measure(byte[] bytes, DateTimeOffset downloadedAt, G2Profile profile, string mode,
        CancellationToken ct)
    {
        profile.Validate(mode);
        ct.ThrowIfCancellationRequested();
        if (bytes.Length == 0 || bytes.LongLength > profile.MaxBytes)
        { throw new G2Failure("BytesLimit"); }
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (mode == "validate" && sha256 != profile.ExpectedSha256)
        { throw new G2Failure("HashMismatch"); }
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new Reader(stream);
        var openMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var meta = reader.Metadata;
        if (!G2Profile.IsSafeType(meta.DatabaseType))
        { throw new G2Failure("UnsafeMetadata"); }
        var builtAt = new DateTimeOffset(meta.BuildDate);
        var epochAdmissible = builtAt <= downloadedAt && downloadedAt <= DateTimeOffset.UtcNow
            && DatabaseAgePolicy.Evaluate(builtAt, DateTimeOffset.UtcNow).CanSuggest;
        var count = 0;
        var ipv4 = 0;
        var ipv6 = 0;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var shape = meta.IPVersion == 6 && meta.BinaryFormatMajorVersion == 2 && meta.BinaryFormatMinorVersion == 0;
        foreach (var network in reader.FindAll<Dictionary<string, object>>(cacheSize: 16))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > profile.MaxNetworks)
            { throw new G2Failure("NetworksLimit"); }
            if (network.Start.AddressFamily == AddressFamily.InterNetwork)
            { ipv4++; }
            else if (network.Start.AddressFamily == AddressFamily.InterNetworkV6)
            { ipv6++; }
            var map = network.Data;
            shape &= map.TryGetValue("country_code", out var code) && code is string text && IpinfoLiteSchema.IsCountryCode(text);
            foreach (var pair in map)
            {
                var known = KnownFields.Contains(pair.Key);
                shape &= known && pair.Value is string;
                if (known)
                { fields.Add(pair.Key); }
            }
        }
        shape &= ipv4 > 0 && ipv6 > 0;
        started = Stopwatch.GetTimestamp();
        using var overlappingStream = new MemoryStream(bytes, writable: false);
        using var overlappingReader = new Reader(overlappingStream);
        if (overlappingReader.Metadata.DatabaseType != meta.DatabaseType)
        { throw new G2Failure("Integrity"); }
        var overlapMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var validationMs = 0d;
        if (mode == "validate")
        {
            started = Stopwatch.GetTimestamp();
            var validator = new CountryDatabaseArtifactValidator(
                new(profile.DatabaseType!, profile.MaxBytes, profile.MaxNetworks), TimeProvider.System);
            validator.Validate(bytes, downloadedAt, ct);
            validationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        using var process = Process.GetCurrentProcess();
        return new(mode, sha256, bytes.LongLength, meta.DatabaseType, builtAt, downloadedAt, count, ipv4, ipv6,
            shape, epochAdmissible, mode == "validate", fields.Order(StringComparer.Ordinal).ToArray(), openMs, overlapMs,
            validationMs, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore, process.PeakWorkingSet64);
    }
}
