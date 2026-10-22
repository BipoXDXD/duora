using System.Net;
using Duora.Core.Geolocation;
using MaxMind.Db;

namespace Duora.Infrastructure.Geolocation;

/// <summary>Only committed manifests are refreshed publicly. The swap waits for any local lookup to finish.</summary>
public sealed class ActiveCountryDatabase(string root, CountryDatabaseArtifactValidator validator, TimeProvider time) : IDisposable
{
    private readonly object _gate = new();
    private Reader? _reader;
    private CountryDatabaseManifest? _manifest;
    private bool _disposed;
    private long _generation;
    private DateTimeOffset _latestUtc = DateTimeOffset.MinValue;

    public async Task<bool> RefreshAsync(CountryDatabaseUpdateStore store, CancellationToken ct)
    {
        var snapshot = await store.ReadReaderSnapshotAsync(ct);
        if (snapshot.Manifest is null)
        {
            lock (_gate)
            {
                if (snapshot.Generation >= _generation)
                {
                    _generation = snapshot.Generation;
                    _reader?.Dispose();
                    _reader = null;
                    _manifest = null;
                }
            }
            return false;
        }
        lock (_gate)
        {
            if (!_disposed && snapshot.Generation == _generation && snapshot.Manifest == _manifest
                && _reader is not null && ObserveClock(out var now) && snapshot.Manifest.IsAdmissible(now))
            { return true; }
        }
        return TryReplace(new CountryDatabaseSnapshot(snapshot.Generation, snapshot.Manifest), ct);
    }

    internal bool TryReplace(CountryDatabaseSnapshot snapshot, CancellationToken ct) => TryReplaceCore(snapshot.Manifest, snapshot.Generation, ct);

    internal bool TryReplace(CountryDatabaseManifest manifest, CancellationToken ct) => TryReplaceCore(manifest, null, ct);

    private bool TryReplaceCore(CountryDatabaseManifest manifest, long? generation, CancellationToken ct)
    {
        Reader? candidate = null;
        try
        {
            lock (_gate)
            {
                if (_disposed || !ObserveClock(out var now) || !manifest.IsAdmissible(now))
                {
                    return false;
                }
            }
            var path = Path.Combine(root, manifest.Sha256 + ".mmdb");
            candidate = validator.OpenVerified(path, manifest, ct);
            lock (_gate)
            {
                if (_disposed || !ObserveClock(out var now) || !manifest.IsAdmissible(now)
                    || generation < _generation || (generation == _generation && _manifest is not null && _manifest.Sha256 != manifest.Sha256))
                {
                    return false;
                }
                var previous = _reader;
                _reader = candidate;
                candidate = null;
                _manifest = manifest;
                if (generation is { } confirmed)
                { _generation = confirmed; }
                previous?.Dispose();
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CountryDatabaseValidationException)
        {
            return false; // No exception detail, IP, country or file contents are logged.
        }
        finally { candidate?.Dispose(); }
    }

    public CountryLookupResult Lookup(IPAddress address)
    {
        lock (_gate)
        {
            if (!ObserveClock(out var now) || _disposed || _reader is null || _manifest is null
                || !DatabaseAgePolicy.Evaluate(_manifest.BuiltAt, now).CanSuggest)
            {
                return new(CountryLookupStatus.Unavailable);
            }
            try
            {
                var code = _reader.Find<CountryEntry>(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)?.CountryCode;
                if (!IpinfoLiteSchema.IsCountryCode(code))
                { return new(CountryLookupStatus.Unknown); }
                return new(CountryLookupStatus.Detected, code);
            }
            catch (ArgumentException) { return new(CountryLookupStatus.Unknown); }
            catch (InvalidDatabaseException) { return new(CountryLookupStatus.Unavailable); }
            catch (DeserializationException) { return new(CountryLookupStatus.Unavailable); }
        }
    }

    // Called under _gate. A backward clock cannot make a previously expired version young again.
    private bool ObserveClock(out DateTimeOffset now)
    {
        now = time.GetUtcNow();
        if (now < _latestUtc)
        {
            return false;
        }
        _latestUtc = now;
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _reader?.Dispose();
            _reader = null;
            _manifest = null;
        }
        GC.SuppressFinalize(this);
    }

    internal sealed class CountryEntry
    {
        [Constructor]
        public CountryEntry([MapKey("country_code")] string countryCode) => CountryCode = countryCode;
        public string CountryCode { get; }
    }
}
