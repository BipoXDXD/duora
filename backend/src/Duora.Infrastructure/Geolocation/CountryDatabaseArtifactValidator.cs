using System.Net.Sockets;
using System.Security.Cryptography;
using MaxMind.Db;

namespace Duora.Infrastructure.Geolocation;

/// <summary>No operational defaults: the actual product metadata and budgets require G2 homologation.</summary>
public sealed record CountryDatabaseValidationProfile(string DatabaseType, long MaxBytes, int MaxNetworks);

public sealed class CountryDatabaseValidationException(string code) : Exception("Country database validation failed.")
{
    public string Code { get; } = code;
}

public sealed class CountryDatabaseArtifactValidator(CountryDatabaseValidationProfile profile, TimeProvider time)
{
    public CountryDatabaseManifest Validate(byte[] bytes, DateTimeOffset downloadedAt, CancellationToken ct)
    {
        using var reader = Open(bytes, downloadedAt, out var manifest, ct);
        return manifest;
    }

    public async Task<CountryDatabaseManifest> ValidateFileAsync(string path, DateTimeOffset downloadedAt, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        if (file.Length <= 0 || file.Length > profile.MaxBytes || file.Length > int.MaxValue)
        {
            throw new CountryDatabaseValidationException("Integrity");
        }
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, ct);
        if (file.ReadByte() != -1) { throw new CountryDatabaseValidationException("Integrity"); }
        return Validate(bytes, downloadedAt, ct);
    }

    internal Reader OpenVerified(string path, CountryDatabaseManifest expected, CancellationToken ct)
    {
        if (!expected.IsAdmissible(time.GetUtcNow()) || expected.SizeBytes > profile.MaxBytes || expected.SizeBytes > int.MaxValue)
        {
            throw new CountryDatabaseValidationException("Integrity");
        }
        // One bounded immutable copy: hashing and parsing see the same bytes even if a disk file is replaced.
        using var file = File.OpenRead(path);
        var bytes = new byte[checked((int)expected.SizeBytes)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) { throw new CountryDatabaseValidationException("Integrity"); }
        var reader = Open(bytes, expected.FirstDownloadedAt, out var actual, ct);
        if (actual with { ValidatedAt = expected.ValidatedAt } != expected)
        {
            reader.Dispose();
            throw new CountryDatabaseValidationException("Integrity");
        }
        return reader;
    }

    private Reader Open(byte[] bytes, DateTimeOffset downloadedAt, out CountryDatabaseManifest manifest, CancellationToken ct)
    {
        if (profile.MaxBytes is <= 0 or > int.MaxValue || profile.MaxNetworks <= 0 || bytes.LongLength > profile.MaxBytes || bytes.Length == 0)
        {
            throw new CountryDatabaseValidationException("Integrity");
        }
        ct.ThrowIfCancellationRequested();
        Reader? reader = null;
        try
        {
            using var memory = new MemoryStream(bytes, writable: false);
            reader = new Reader(memory);
            var meta = reader.Metadata;
            if (meta.DatabaseType != profile.DatabaseType || meta.IPVersion != 6 || meta.BinaryFormatMajorVersion != 2 || meta.BinaryFormatMinorVersion != 0)
            {
                throw new CountryDatabaseValidationException("Schema");
            }
            manifest = new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength,
                new DateTimeOffset(meta.BuildDate), downloadedAt, time.GetUtcNow(), meta.DatabaseType);
            if (!manifest.IsAdmissible(time.GetUtcNow())) { throw new CountryDatabaseValidationException("Integrity"); }
            var count = 0; var ipv4 = false; var ipv6 = false;
            foreach (var network in reader.FindAll<Dictionary<string, object>>(cacheSize: 16))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > profile.MaxNetworks) { throw new CountryDatabaseValidationException("Integrity"); }
                var map = network.Data;
                if (!map.TryGetValue("country", out var country) || country is not string
                    || map.Any(p => p.Key is not ("country" or "country_name" or "continent" or "continent_name") || p.Value is not string))
                {
                    throw new CountryDatabaseValidationException("Schema");
                }
                ipv4 |= network.Start.AddressFamily == AddressFamily.InterNetwork;
                ipv6 |= network.Start.AddressFamily == AddressFamily.InterNetworkV6;
            }
            if (!ipv4 || !ipv6) { throw new CountryDatabaseValidationException("Schema"); }
            return reader;
        }
        catch (Exception error) when (error is InvalidDatabaseException or DeserializationException or InvalidDataException or ArgumentException or OverflowException)
        {
            reader?.Dispose();
            throw new CountryDatabaseValidationException("Integrity");
        }
        catch
        {
            reader?.Dispose();
            throw;
        }
    }
}
