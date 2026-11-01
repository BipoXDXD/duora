using System.Runtime.Versioning;
using System.Security.Cryptography;
using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

[SupportedOSPlatform("linux")]
internal sealed class PrivateGenerationAuthorityEngine(
    PrivateGenerationLayout layout, PrivateGenerationReference reference, PrivateGenerationAuthorityMetadata metadata) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PrivateGenerationJournal _journal = new(layout);
    private readonly FileSystemObjectStore _files = new(layout.DataRoot, createMissingDirectories: false);

    public void Dispose() => _gate.Dispose();

    internal async Task<(PrivateGenerationReply Reply, byte[] Body)> HandleAsync(PrivateGenerationPeer peer,
        PrivateGenerationRequest request, Stream body, CancellationToken cancellationToken)
    {
        if (request.Version != 1 || !layout.Enrollment.ClientUids.Contains((uint)peer.Uid)
            || peer.Uid != peer.Gid || request.Length is < 0 or > 16_777_216
            || request.Operation is not ("register" or "put" or "seal" or "close" or "sweep" or "read" or "delete"))
        {
            throw new InvalidDataException("Unadmitted storage request.");
        }
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return (new("busy"), []);
        }
        try
        {
            layout.Validate();
            if (request.Operation is "read" or "delete")
            {
                return await ReadOrDeleteAsync(request, cancellationToken);
            }
            var identity = request.Identity ?? throw new InvalidDataException("Missing generation identity.");
            var record = await metadata.ReadAsync(identity.Area, identity.Generation, cancellationToken)
                ?? throw new InvalidDataException("Unknown storage manifest.");
            if (record.Manifest.Identity != identity)
            {
                throw new InvalidDataException("Persisted storage manifest mismatch.");
            }
            request.Manifest?.Validate();
            if (request.Manifest is not null && request.Manifest.Identity != identity)
            {
                throw new InvalidDataException("Request manifest mismatch.");
            }
            if (request.Operation != "put" && request.Length != 0)
            {
                throw new InvalidDataException("Unexpected private storage body.");
            }
            if (request.Operation == "register")
            {
                RequireWritable(record);
                var state = await reference.TransitionAsync(identity, "Open", cancellationToken);
                _journal.Persist(identity, "Open", state.Revision);
            }
            else if (request.Operation == "put")
            {
                RequireWritable(record);
                var state = await reference.TransitionAsync(identity, "Read", cancellationToken);
                if (state.State != "Open" || _journal.Find(identity)?.State != "Open")
                {
                    throw new InvalidDataException("Generation is not open.");
                }
                var variant = record.Manifest.Variants.Single(v => v.Kind == request.Variant);
                if (request.Length != variant.Length)
                {
                    throw new InvalidDataException("Private variant envelope mismatch.");
                }
                var bytes = new byte[checked((int)variant.Length)];
                try
                {
                    // Gate covers network EOF, every filesystem I/O and disposal, including the adapter's rename.
                    await PrivateGenerationWire.WriteAsync(body, new PrivateGenerationReply("admitted"), cancellationToken);
                    Console.WriteLine("STORAGE_IO_ADMITTED");
                    await body.ReadExactlyAsync(bytes, cancellationToken);
                    if (await body.ReadAsync(new byte[1], cancellationToken) != 0
                        || Convert.ToHexStringLower(SHA256.HashData(bytes)) != variant.Sha256)
                    {
                        throw new InvalidDataException("Private variant body mismatch.");
                    }
                    var key = record.Manifest.Key(variant.Kind);
                    await using var input = new MemoryStream(bytes, writable: false);
                    await _files.PutAsync(key, input, bytes.LongLength, "image/webp", cancellationToken);
                    File.SetUnixFileMode(Path.Combine(layout.DataRoot, key.Value),
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    SynchronizeFile(key);
                    layout.SyncArea(identity.Area);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            else if (request.Operation == "seal")
            {
                RequireWritable(record);
                var state = await reference.TransitionAsync(identity, "Read", cancellationToken);
                if (state.State is not ("Open" or "Sealed"))
                {
                    throw new InvalidDataException("Closed generation cannot seal.");
                }
                foreach (var variant in record.Manifest.Variants)
                {
                    await using var file = OpenVerified(record.Manifest.Key(variant.Kind));
                    if (file.Length != variant.Length || Convert.ToHexStringLower(
                        await SHA256.HashDataAsync(file, cancellationToken)) != variant.Sha256)
                    {
                        throw new InvalidDataException("Incomplete private generation.");
                    }
                    SynchronizeFile(record.Manifest.Key(variant.Kind));
                }
                layout.SyncArea(identity.Area);
                var sealedState = await reference.TransitionAsync(identity, "Sealed", cancellationToken);
                _journal.Persist(identity, "Sealed", sealedState.Revision);
            }
            else if (request.Operation == "close")
            {
                var closed = await CloseAsync(record, cancellationToken);
                return (new("ok", closed.Revision), []);
            }
            else
            {
                var state = await reference.TransitionAsync(identity, "Read", cancellationToken);
                var local = _journal.Find(identity);
                if (!record.CanClose || state.State != "Closed" || state.Revision != request.ClosedRevision
                    || local?.State != "Closed" || local.Revision != state.Revision || request.CleanupToken == Guid.Empty)
                {
                    throw new InvalidDataException("Sweep requires a prior matching closure.");
                }
                _journal.Synchronize();
                foreach (var kind in PhotoVariants.All)
                {
                    var key = record.Manifest.Key(kind);
                    await _files.DeleteAsync(key, cancellationToken);
                    var directory = Path.GetDirectoryName(Path.Combine(layout.DataRoot, key.Value))!;
                    foreach (var temporary in Directory.EnumerateFiles(directory, Path.GetFileName(key.Value) + ".*.partial"))
                    {
                        var suffix = Path.GetFileName(temporary)[(Path.GetFileName(key.Value).Length + 1)..];
                        if (suffix.Length == 40 && suffix.EndsWith(".partial", StringComparison.Ordinal)
                            && suffix[..32].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                        {
                            File.Delete(temporary);
                        }
                    }
                }
                layout.SyncArea(identity.Area);
                var proof = await metadata.RecordSweepAsync(new(identity, state.Revision),
                    request.CleanupToken, cancellationToken);
                return (new("ok", state.Revision, proof), []);
            }
            return (new("ok"), []);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PrivateGenerationCloseReceipt> CloseAsync(PrivateGenerationStorageRecord record,
        CancellationToken cancellationToken)
    {
        if (!record.CanClose)
        {
            throw new InvalidDataException("Business generation is not eligible for closure.");
        }
        var identity = record.Manifest.Identity;
        var state = await reference.TransitionAsync(identity, "Closing", cancellationToken);
        var local = _journal.Find(identity);
        if (local?.State != "Closed")
        {
            _journal.Persist(identity, "Closing", state.Revision);
        }
        _journal.Persist(identity, "Closed", state.Revision);
        // Durable external revocation already blocks admission. This final commit follows local sync and drained I/O.
        var closed = await reference.TransitionAsync(identity, "Closed", cancellationToken);
        if (closed.State != "Closed" || closed.Revision != state.Revision)
        {
            throw new InvalidDataException("Storage closure mismatch.");
        }
        return new(identity, closed.Revision);
    }

    private async Task<(PrivateGenerationReply Reply, byte[] Body)> ReadOrDeleteAsync(
        PrivateGenerationRequest request, CancellationToken cancellationToken)
    {
        if (request.Length != 0 || request.ObjectKey is null)
        {
            throw new InvalidDataException("Unexpected private object request.");
        }
        var key = ObjectKey.Parse(request.ObjectKey);
        if (!PrivateGenerationManifest.TryPrivateKey(key, out var area, out var generation, out var variant))
        {
            throw new InvalidDataException("Not a private object key.");
        }
        var record = await metadata.ReadAsync(area, generation, cancellationToken)
            ?? throw new InvalidDataException("No admitted private object binding.");
        if (request.Operation == "delete")
        {
            if (!record.CanDelete)
            {
                throw new InvalidDataException("No committed asset deletion obligation.");
            }
            await CloseAsync(record, cancellationToken);
            await _files.DeleteAsync(key, cancellationToken);
            layout.SyncArea(area);
            return (new("ok"), []);
        }
        var state = await reference.TransitionAsync(record.Manifest.Identity, "Read", cancellationToken);
        if (state.State is "Closing" or "Closed")
        {
            return (new("missing"), []);
        }
        try
        {
            await using var file = OpenVerified(key);
            var expected = record.Manifest.Variants.Single(v => v.Kind == variant);
            if (file.Length != expected.Length)
            {
                throw new InvalidDataException("Private object length mismatch.");
            }
            var bytes = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(bytes, cancellationToken);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected.Sha256)
            {
                throw new InvalidDataException("Private object digest mismatch.");
            }
            return (new("ok", Length: bytes.Length), bytes);
        }
        catch (FileNotFoundException)
        {
            return (new("missing"), []);
        }
    }

    private FileStream OpenVerified(ObjectKey key)
    {
        using var directory = PrivateGenerationLinux.OpenDirectory(layout.DataRoot);
        using var area = PrivateGenerationLinux.OpenChild(directory, Path.GetDirectoryName(key.Value)!, directory: true);
        var handle = PrivateGenerationLinux.OpenChild(area, Path.GetFileName(key.Value), directory: false);
        try
        {
            var info = PrivateGenerationLinux.Stat(handle);
            if ((info.Mode & 0xf000) != 0x8000 || info.Uid != layout.Enrollment.AuthorityUid || info.Links != 1)
            {
                throw new InvalidDataException("Untrusted private object.");
            }
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private void SynchronizeFile(ObjectKey key)
    {
        using var file = OpenVerified(key);
        PrivateGenerationLinux.Sync(file.SafeFileHandle);
    }

    private void RequireWritable(PrivateGenerationStorageRecord record)
    {
        if (!record.CanWrite || record.Manifest.Identity.Domain != layout.Enrollment.Domain)
        {
            throw new InvalidDataException("Private generation admission is revoked.");
        }
    }
}
