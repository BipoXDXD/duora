using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Duora.Infrastructure.Geolocation;

public enum CountryDatabaseUpdateOutcome { Promoted, Unchanged, Failed, LostLease }

/// <summary>External work stays outside SQL transactions. Only a fenced commit may publish its manifest.</summary>
public sealed partial class CountryDatabaseUpdateService(
    CountryDatabaseUpdateStore store, CountryDatabaseDownloader downloader, CountryDatabaseArtifactValidator validator,
    string root, string? approvedInitialHash = null, ILogger<CountryDatabaseUpdateService>? logger = null)
{
    public async Task<CountryDatabaseUpdateOutcome> ExecuteAsync(CountryDatabaseUpdateLease lease, CancellationToken ct)
    {
        var attempt = Path.Combine(root, ".attempt-" + Guid.NewGuid().ToString("N"));
        try
        {
            EnsurePrivateDirectory(root);
            EnsurePrivateDirectory(attempt);
            var path = Path.Combine(attempt, "candidate.mmdb");
            var downloadedAt = await downloader.DownloadAsync(path, ct);
            var manifest = await validator.ValidateFileAsync(path, downloadedAt, ct);
            // Durable first-download metadata precedes publication. A stale worker may not even admit a candidate.
            var canonical = await store.RecordCandidateAsync(lease, manifest, ct);
            if (canonical is null)
            { return CountryDatabaseUpdateOutcome.LostLease; }
            var target = Path.Combine(root, canonical.Sha256 + ".mmdb");
            try
            { File.Move(path, target, overwrite: false); }
            catch (IOException) when (File.Exists(target))
            {
                // Reuse an existing immutable artifact only after verifying the exact bytes and original manifest.
                using var verified = validator.OpenVerified(target, canonical, ct);
            }
            if (!OperatingSystem.IsWindows())
            { File.SetUnixFileMode(target, UnixFileMode.UserRead); }
            var promotion = await store.PromoteAsync(lease, canonical, approvedInitialHash, ct);
            return promotion switch
            {
                CountryDatabasePromotion.Promoted => CountryDatabaseUpdateOutcome.Promoted,
                CountryDatabasePromotion.Unchanged => CountryDatabaseUpdateOutcome.Unchanged,
                CountryDatabasePromotion.LostLease => CountryDatabaseUpdateOutcome.LostLease,
                _ => await Failure(lease, promotion == CountryDatabasePromotion.RequiresApproval ? "ApprovalRequired" : "Integrity", false, null, ct),
            };
        }
        catch (CountryDatabaseUpdateException error) { return await Failure(lease, error.Code, error.Transient, error.RetryAfter, ct); }
        catch (CountryDatabaseValidationException error) { return await Failure(lease, error.Code, false, null, ct); }
        catch (IOException) { return await Failure(lease, "Disk", true, null, ct); }
        catch (UnauthorizedAccessException) { return await Failure(lease, "Disk", false, null, ct); }
        finally
        {
            try
            { if (Directory.Exists(attempt)) { Directory.Delete(attempt, recursive: true); } }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Never leak a path/provider exception. A future operations sweep must reconcile crash/IO orphans.
                LogCleanup(logger ?? NullLogger<CountryDatabaseUpdateService>.Instance);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Country database staging cleanup requires review.")]
    private static partial void LogCleanup(ILogger logger);

    private async Task<CountryDatabaseUpdateOutcome> Failure(CountryDatabaseUpdateLease lease, string code, bool transient, DateTimeOffset? retryAfter, CancellationToken ct)
    {
        return await store.FailAsync(lease, code, transient, retryAfter, ct) ? CountryDatabaseUpdateOutcome.Failed : CountryDatabaseUpdateOutcome.LostLease;
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        var existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            if (!existed)
            { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            var publicBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(directory) & publicBits) != 0)
            { throw new CountryDatabaseUpdateException("Validation", false); }
        }
    }
}
