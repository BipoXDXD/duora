using Duora.Infrastructure.Audit;

namespace Duora.Infrastructure.Geolocation;

/// <summary>Offline validation and fenced restoration. No endpoint or operator command is exposed.</summary>
public sealed class CountryDatabaseRecoveryService(CountryDatabaseOperationsStore store,
    CountryDatabaseArtifactValidator validator, string root, ICountryDatabaseArchive? archive = null, bool enabled = false)
{
    public async Task<CountryDatabaseRecoveryOutcome> RecoverAsync(CancellationToken ct)
    {
        if (!enabled)
        { return CountryDatabaseRecoveryOutcome.Disabled; }
        await using var files = await store.LockFilesAsync(ct);
        var s = await store.ReadAsync(ct);
        if (store.UtcNow < s.LastObserved || s.LastAttempt > store.UtcNow)
        { return CountryDatabaseRecoveryOutcome.ClockInvalid; }
        if (s.ReviewRequired || s.Blocked)
        { return CountryDatabaseRecoveryOutcome.ReviewRequired; }
        // Never select an older build merely because the newest approved file is missing.
        var m = s.Approved.Where(m => !s.Retired.Contains(m.Sha256))
            .OrderByDescending(m => m.BuiltAt).FirstOrDefault();
        if (m is null)
        { return CountryDatabaseRecoveryOutcome.Unavailable; }
        if (!m.IsAdmissible(store.UtcNow))
        {
            if (m.HasCoherentMetadata(store.UtcNow) && store.UtcNow - m.BuiltAt >= TimeSpan.FromHours(168))
            { return CountryDatabaseRecoveryOutcome.Unavailable; }
            await store.HoldAsync(s.Generation, ct);
            return CountryDatabaseRecoveryOutcome.ReviewRequired;
        }
        var path = Path.Combine(root, m.Sha256 + ".mmdb");
        if (File.Exists(path))
        {
            try
            {
                using var verified = validator.OpenVerified(path, m, ct);
                return await Commit(s.Generation, m, ct);
            }
            catch (CountryDatabaseValidationException) when (s.RepairAuthorized) { /* Explicit reviewed repair below. */ }
            catch (IOException) when (s.RepairAuthorized) { }
            catch (CountryDatabaseValidationException)
            {
                await store.HoldAsync(s.Generation, ct);
                return CountryDatabaseRecoveryOutcome.ReviewRequired;
            }
            catch (IOException)
            {
                await store.HoldAsync(s.Generation, ct);
                return CountryDatabaseRecoveryOutcome.ReviewRequired;
            }
        }
        if (archive is null)
        { return CountryDatabaseRecoveryOutcome.Unavailable; }
        await using var copy = await archive.OpenAsync(m.Sha256, ct);
        if (copy is null)
        { return CountryDatabaseRecoveryOutcome.Unavailable; }
        CountryDatabaseUpdateService.EnsurePrivateDirectory(root);
        var attempt = Path.Combine(root, ".attempt-recovery-" + Guid.NewGuid().ToString("N"));
        CountryDatabaseUpdateService.EnsurePrivateDirectory(attempt);
        try
        {
            var candidate = Path.Combine(attempt, "candidate.mmdb");
            if (m.SizeBytes <= 0 || m.SizeBytes > validator.MaxBytes || m.SizeBytes > int.MaxValue)
            { throw new CountryDatabaseValidationException("Integrity"); }
            await using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                long written = 0;
                int read;
                while ((read = await copy.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (written > m.SizeBytes)
                    { throw new CountryDatabaseValidationException("Integrity"); }
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (written != m.SizeBytes)
                { throw new CountryDatabaseValidationException("Integrity"); }
                output.Flush(flushToDisk: true);
            }
            using (validator.OpenVerified(candidate, m, ct))
            { }
            File.Move(candidate, path, overwrite: s.RepairAuthorized);
            if (!OperatingSystem.IsWindows())
            { File.SetUnixFileMode(path, UnixFileMode.UserRead); }
            return await Commit(s.Generation, m, ct);
        }
        catch (CountryDatabaseValidationException)
        {
            await store.HoldAsync(s.Generation, ct);
            return CountryDatabaseRecoveryOutcome.ReviewRequired;
        }
        finally
        { Directory.Delete(attempt, recursive: true); }
    }

    /// <summary>Future trusted operator adapter must authenticate/authorize before calling; never a participant API.</summary>
    public async Task<bool> ReleaseAsync(long expectedGeneration, Guid reviewId, OperatorName actor, CancellationToken ct)
    {
        if (!enabled)
        { return false; }
        await using var files = await store.LockFilesAsync(ct);
        return await store.ReleaseAsync(expectedGeneration, reviewId, actor, ct);
    }

    private async Task<CountryDatabaseRecoveryOutcome> Commit(long generation, CountryDatabaseManifest manifest, CancellationToken ct) =>
        await store.RecoverAsync(generation, manifest, ct) ? CountryDatabaseRecoveryOutcome.Recovered : CountryDatabaseRecoveryOutcome.Unavailable;
}
