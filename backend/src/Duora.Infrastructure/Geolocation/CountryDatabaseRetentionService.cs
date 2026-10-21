using System.Security.Cryptography;

namespace Duora.Infrastructure.Geolocation;

/// <summary>Explicit maintenance only. No scheduler or separate storage is configured by this component.</summary>
public sealed class CountryDatabaseRetentionService(CountryDatabaseOperationsStore store, string root,
    ICountryDatabaseArchive? archive = null, bool enabled = false)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!enabled)
        { return; }
        await using var files = await store.LockFilesAsync(ct);
        var s = await store.ReadAsync(ct);
        if (store.UtcNow < s.LastObserved)
        { return; }
        var keep = await store.RetireAsync(s, ct);
        // Reader handles own immutable memory, not these disk files. Existing lookups never use an unlinked handle.
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.GetFiles(root, "*.mmdb"))
            {
                ct.ThrowIfCancellationRequested();
                if (!keep.Contains(Path.GetFileNameWithoutExtension(path)))
                { File.Delete(path); }
            }
            if (!s.InFlight)
            {
                foreach (var attempt in Directory.GetDirectories(root, ".attempt-*"))
                { Directory.Delete(attempt, recursive: true); }
            }
        }
        if (archive is null)
        { return; }
        var copies = s.Approved.Where(m => keep.Contains(m.Sha256))
            .OrderByDescending(m => m.Sha256 == s.Active?.Sha256).ThenByDescending(m => m.BuiltAt).Take(2).ToArray();
        var copyHashes = copies.Select(m => m.Sha256).ToHashSet(StringComparer.Ordinal);
        // Delete first: failure may reduce redundancy, never silently extend retention or exceed the copy budget.
        foreach (var hash in await archive.ListAsync(ct))
        {
            if (!copyHashes.Contains(hash))
            { await archive.DeleteAsync(hash, ct); }
        }
        foreach (var m in copies)
        {
            var path = Path.Combine(root, m.Sha256 + ".mmdb");
            if (!File.Exists(path))
            { continue; }
            await using var file = File.OpenRead(path);
            if (file.Length != m.SizeBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)) != m.Sha256)
            {
                await store.HoldAsync(s.Generation, ct);
                return;
            }
            file.Position = 0;
            await archive.PutAsync(m, file, ct);
        }
    }
}
