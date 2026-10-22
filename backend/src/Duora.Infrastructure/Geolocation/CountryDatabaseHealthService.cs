namespace Duora.Infrastructure.Geolocation;

/// <summary>Independent explicit invocation; no host registration, timer or production notification adapter.</summary>
public sealed class CountryDatabaseHealthService(CountryDatabaseOperationsStore store,
    CountryDatabaseArtifactValidator validator, string root, ICountryDatabaseNotifier notifier, bool enabled = false)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!enabled)
        { return; }
        var clockValid = true;
        // Health never waits on publication/archive I/O. Generation is revalidated before recording conditions.
        var s = await store.ReadAsync(ct);
        var now = store.UtcNow;
        var conditions = new Dictionary<string, int>(StringComparer.Ordinal);
        var integrity = s.ReviewRequired;
        if (now < s.LastObserved)
        {
            conditions["Clock"] = 2;
            clockValid = false;
        }
        else
        {
            if (s.LastAttempt is null || now - s.LastAttempt >= TimeSpan.FromHours(36))
            { conditions["NoExecution"] = 1; }
            if (s.LastAttempt > now)
            { conditions["Clock"] = 2; }
            if (s.Active is not { } m)
            { conditions["MissingBase"] = 2; }
            else
            {
                var age = now - m.BuiltAt;
                if (age >= TimeSpan.FromHours(72))
                { conditions["Age"] = age >= TimeSpan.FromHours(168) ? 2 : 1; }
                if (!m.HasCoherentMetadata(now))
                { integrity = true; }
                else if (!File.Exists(Path.Combine(root, m.Sha256 + ".mmdb")))
                { conditions["MissingBase"] = 2; }
                else if (m.IsAdmissible(now))
                {
                    try
                    { using var verified = validator.OpenVerified(Path.Combine(root, m.Sha256 + ".mmdb"), m, ct); }
                    catch (CountryDatabaseValidationException)
                    { integrity = true; }
                    catch (FileNotFoundException)
                    { conditions["MissingBase"] = 2; }
                    catch (DirectoryNotFoundException)
                    { conditions["MissingBase"] = 2; }
                    catch (IOException)
                    { integrity = true; }
                    catch (UnauthorizedAccessException)
                    { conditions["Disk"] = 2; }
                }
            }
            if (s.Blocked && s.Error is "Unauthorized" or "Schema" or "Integrity" or "ApprovalRequired" or "Validation" or "Disk")
            { conditions[s.Error] = 2; }
            if (s.Exhausted)
            { conditions["Exhausted"] = 1; }
        }
        if (integrity)
        { conditions["Integrity"] = 2; }
        await store.RecordHealthAsync(s, conditions, integrity, ct);
        if (clockValid)
        { await store.DeliverAsync(notifier, ct); }
    }
}
