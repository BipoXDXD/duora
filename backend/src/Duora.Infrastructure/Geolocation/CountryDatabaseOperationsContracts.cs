namespace Duora.Infrastructure.Geolocation;

/// <summary>Sanitized operational event. Id is the durable idempotency key, not a participant identifier.</summary>
public sealed record CountryDatabaseNotification(long Id, string Code, int Severity, bool Recovered, DateTimeOffset OccurredAt);

/// <summary>Future transports must deduplicate Id across crashes/retries. No production adapter is supplied.</summary>
public interface ICountryDatabaseNotifier
{
    Task SendAsync(CountryDatabaseNotification notification, CancellationToken ct);
}

/// <summary>
/// Private, separate storage: immutable objects keyed by SHA, original manifests and license retained by the adapter.
/// List includes incomplete/orphan objects; Delete must remove all replicas/versions of that key. No credentials in backups.
/// No real adapter, resource or transport is provisioned by this contract.
/// </summary>
public interface ICountryDatabaseArchive
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct);
    Task PutAsync(CountryDatabaseManifest manifest, Stream content, CancellationToken ct);
    Task<Stream?> OpenAsync(string hash, CancellationToken ct);
    Task DeleteAsync(string hash, CancellationToken ct);
}

public enum CountryDatabaseRecoveryOutcome { Disabled, Recovered, Unavailable, ReviewRequired, ClockInvalid }

internal sealed record CountryDatabaseOperationsSnapshot(long Generation, CountryDatabaseManifest? Active,
    DateTimeOffset? LastAttempt, string? Error, bool Blocked, bool ReviewRequired, bool RepairAuthorized,
    DateTimeOffset LastObserved, bool InFlight, bool Exhausted,
    IReadOnlyList<CountryDatabaseManifest> Approved, IReadOnlyList<CountryDatabaseManifest> Versions,
    IReadOnlySet<string> Retired);
