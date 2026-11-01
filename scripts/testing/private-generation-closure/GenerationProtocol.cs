using System.Runtime.Versioning;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Microsoft.Win32.SafeHandles;

namespace Duora.ClosureProbe;

/// <summary>
/// Throwaway cooperative protocol, NOT a storage capability. Same-UID bypass, control rollback,
/// symlink/control replacement by a writer, host crash and operational restore remain unqualified.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class GenerationProtocol(string root, string area, Guid generation, Guid domain, Barrier barrier)
{
    private readonly string _control = Path.Combine(root, "control", $"{area}-{generation:N}");
    private readonly string _data = Path.Combine(root, "data");
    private readonly string _identity = $"duora-closure-probe-v1\n{domain:N}\n{area}\n{generation:N}\n";

    private string AreaDirectory => Path.Combine(_data, area == "candidate" ? "review-candidates" : "derivatives");

    // Only a synthetic offline fixture can enroll. This is NOT a retry or SQL admission API.
    // A missing/damaged existing record is never automatically initialized by write/close/sweep.
    internal void ReserveOffline()
    {
        if (Directory.Exists(_control))
        {
            throw new InvalidDataException("Reservation already known; reconciliation required.");
        }

        Directory.CreateDirectory(_control);
        Directory.CreateDirectory(AreaDirectory);
        PersistNew(Path.Combine(_control, "lock"), []);
        PersistNew(Path.Combine(_control, "reservation"), System.Text.Encoding.UTF8.GetBytes(_identity));
        LinuxStorage.SyncDirectory(_control);
        LinuxStorage.SyncDirectory(Path.GetDirectoryName(_control)!);
        LinuxStorage.SyncDirectory(AreaDirectory);
        LinuxStorage.SyncDirectory(_data);
        LinuxStorage.SyncDirectory(root);
    }

    internal SafeFileHandle? TryAcquire()
    {
        // OPEN, never create: replacing an unlinked lock would create a second authority.
        var handle = LinuxStorage.OpenLock(Path.Combine(_control, "lock"));
        if (!LinuxStorage.TryLock(handle))
        {
            handle.Dispose();
            return null;
        }

        try
        {
            RequireRecord("reservation", _identity);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void RequireOpen()
    {
        if (File.Exists(Path.Combine(_control, "closed")))
        {
            throw new InvalidDataException("Generation closed.");
        }
    }

    internal async Task WriteAsync()
    {
        var store = new FileSystemObjectStore(_data, createMissingDirectories: false);
        foreach (var kind in PhotoVariants.All)
        {
            barrier.Wait("before-put", (int)kind);
            await using var content = new PausedInput(barrier, (int)kind);
            await store.PutAsync(Key(kind), content, content.Length, "application/octet-stream", CancellationToken.None);
            barrier.Wait("after-put", (int)kind);
        }
        // All awaited I/O and all data descriptors are disposed before releasing the generation lock.
        // No ACK is invented: this executable has no database access.
    }

    internal void Close()
    {
        var closed = Path.Combine(_control, "closed");
        if (!File.Exists(closed))
        {
            var pending = Path.Combine(_control, "closing");
            // Any unacknowledged pending file is rewritten under the SAME stable lock.
            using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(System.Text.Encoding.UTF8.GetBytes(_identity + "closed\n"));
                file.Flush(flushToDisk: true);
            }

            barrier.Wait("marker-flushed");
            File.Move(pending, closed, overwrite: false);
            barrier.Wait("marker-renamed");
        }

        RequireRecord("closed", _identity + "closed\n");
        // Also sync on retry: a previous process may have died between rename and directory fsync.
        using (var file = new FileStream(closed, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            file.Flush(flushToDisk: true);
        }

        barrier.Wait("close-sync");
        LinuxStorage.SyncDirectory(_control);
        barrier.Wait("directory-synced");
    }

    internal async Task SweepAsync()
    {
        RequireRecord("closed", _identity + "closed\n"); // No sweep can manufacture a closure.
        Close(); // Reestablish durability before deleting; no fresh evidence during a prior sweep.
        var store = new FileSystemObjectStore(_data, createMissingDirectories: false);
        foreach (var kind in PhotoVariants.All)
        {
            var key = Key(kind);
            await store.DeleteAsync(key, CancellationToken.None);
            var path = Path.Combine(_data, key.Value);
            foreach (var temporary in Directory.EnumerateFiles(AreaDirectory, Path.GetFileName(path) + ".*.partial"))
            {
                var suffix = Path.GetFileName(temporary)[(Path.GetFileName(path).Length + 1)..];
                // Exactly the current adapter's random 32-hex suffix, not prefix/age based deletion.
                if (suffix.Length == 40 && suffix.EndsWith(".partial", StringComparison.Ordinal)
                    && suffix[..32].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    File.Delete(temporary);
                }
            }

            barrier.Wait("after-delete", (int)kind);
        }

        barrier.Wait("removal-sync");
        LinuxStorage.SyncDirectory(AreaDirectory);
    }

    private ObjectKey Key(PhotoVariant kind) => area == "candidate"
        ? ObjectKey.ForReviewCandidate(generation, kind) : ObjectKey.ForPromotedDerivative(generation, kind);

    private void RequireRecord(string name, string expected)
    {
        if (File.ReadAllText(Path.Combine(_control, name)) != expected)
        {
            throw new InvalidDataException("Missing, corrupt or incompatible control.");
        }
    }

    private static void PersistNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
}
