using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

/// <summary>Private signed metadata contract, never a public DTO or authority inferred from a restored database.</summary>
internal sealed record ApprovedMediaRestoreManifest(int Schema, Guid EnvironmentId, Guid SnapshotId,
    string SourceDigest, DateTimeOffset CapturedAt, string DatabaseSnapshot,
    IReadOnlyList<ApprovedMediaCatalogAsset> Assets, long TotalBytes, string KeyId, byte[] Signature)
{
    public override string ToString() => nameof(ApprovedMediaRestoreManifest);
}

/// <summary>Independent custody inputs; the caller must not derive these pins from the manifest being checked.</summary>
internal sealed record ApprovedMediaRestoreExpectation(Guid EnvironmentId, Guid SnapshotId,
    string SourceDigest, int MaximumAssets, long MaximumBytes)
{
    public override string ToString() => nameof(ApprovedMediaRestoreExpectation);
}

/// <summary>Trusted adapter boundary: authenticate every field of the frozen complete manifest.</summary>
internal interface IApprovedMediaRestoreManifestAuthenticator
{
    bool Verify(ApprovedMediaRestoreManifest manifest);
}

internal abstract record ApprovedMediaRestoreSelectionResult
{
    internal sealed record Selected(Guid EnvironmentId, Guid SnapshotId, Guid RestoreEpoch,
        IReadOnlyList<ApprovedMediaCatalogAsset> Assets, long TotalBytes) : ApprovedMediaRestoreSelectionResult
    {
        public override string ToString() => "ApprovedMediaRestoreSelection";
    }

    internal sealed record Refused : ApprovedMediaRestoreSelectionResult;
}

/// <summary>Private selection only; never copies bytes, writes SQL or admits a restore.</summary>
internal static class ApprovedMediaRestoreSelection
{
    private const int MaximumAssets = 4096;
    private const int MaximumLedgerEntries = 65_536;
    private const long MaximumAssetBytes = 24 * 1024 * 1024;
    private const long MaximumVariantBytes = 16 * 1024 * 1024;

    internal static ApprovedMediaRestoreSelectionResult Select(ApprovedMediaCatalog catalog,
        ApprovedMediaRestoreManifest manifest, ApprovedMediaRestoreExpectation expected,
        DeletionLedgerExpectation ledgerExpected, DeletionLedgerBarrier barrier,
        IEnumerable<DeletionLedgerEntry> entries, IApprovedMediaRestoreManifestAuthenticator? manifestAuthenticator,
        IDeletionLedgerBarrierAuthenticator? ledgerAuthenticator, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SelectCore(catalog, manifest, expected, ledgerExpected, barrier, entries,
                manifestAuthenticator, ledgerAuthenticator, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ApprovedMediaRestoreSelectionResult.Refused();
        }
    }

    private static ApprovedMediaRestoreSelectionResult SelectCore(ApprovedMediaCatalog catalog,
        ApprovedMediaRestoreManifest manifest, ApprovedMediaRestoreExpectation expected,
        DeletionLedgerExpectation ledgerExpected, DeletionLedgerBarrier barrier,
        IEnumerable<DeletionLedgerEntry> entries, IApprovedMediaRestoreManifestAuthenticator? manifestAuthenticator,
        IDeletionLedgerBarrierAuthenticator? ledgerAuthenticator, CancellationToken cancellationToken)
    {
        if (catalog is null || manifest is null || expected is null || ledgerExpected is null || barrier is null
            || entries is null || manifestAuthenticator is null || ledgerAuthenticator is null
            || expected.EnvironmentId == Guid.Empty || expected.SnapshotId == Guid.Empty || !IsSourceDigest(expected.SourceDigest)
            || expected.MaximumAssets is < 1 or > MaximumAssets
            || expected.MaximumBytes is < 1 or > MaximumAssets * MaximumAssetBytes
            || ledgerExpected.EnvironmentId != expected.EnvironmentId
            || ledgerExpected.MaximumEntries is < 1 or > MaximumLedgerEntries
            || ledgerExpected.MinimumChainRoot is not { Length: 32 }
            || barrier.ChainRoot is not { Length: 32 } || barrier.Signature is not { Length: > 0 and <= 4096 }
            || barrier.KeyId is not { Length: > 0 and <= 128 }
            || manifest.Schema != 1 || manifest.EnvironmentId != expected.EnvironmentId
            || manifest.SnapshotId != expected.SnapshotId || manifest.SourceDigest != expected.SourceDigest
            || !ValidCapture(manifest.CapturedAt, manifest.DatabaseSnapshot)
            || !ValidCapture(catalog.CapturedAt, catalog.Snapshot)
            || manifest.Signature is not { Length: > 0 and <= 4096 }
            || manifest.KeyId is not { Length: > 0 and <= 128 }
            || manifest.KeyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
        {
            throw Inconsistent();
        }

        // All caller-owned arrays and collections are frozen before either authentication callback.
        ledgerExpected = ledgerExpected with { MinimumChainRoot = [.. ledgerExpected.MinimumChainRoot] };
        barrier = barrier with { ChainRoot = [.. barrier.ChainRoot], Signature = [.. barrier.Signature] };
        var catalogAssets = FreezeAssets(catalog.Assets, expected, false, cancellationToken, out var catalogBytes);
        var manifestAssets = FreezeAssets(manifest.Assets, expected, true, cancellationToken, out var manifestBytes);
        if (catalogBytes != catalog.TotalBytes || manifestBytes != manifest.TotalBytes)
        {
            throw Inconsistent();
        }
        manifest = manifest with { Assets = manifestAssets, Signature = [.. manifest.Signature] };
        var authenticated = manifestAuthenticator.Verify(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        if (!authenticated)
        {
            throw Inconsistent();
        }

        var ledger = DeletionLedgerValidation.Validate(ledgerExpected, barrier,
            ObserveCancellation(entries, cancellationToken), ledgerAuthenticator);
        // The ledger deliberately converts enumeration failures to Refused; caller cancellation still propagates.
        cancellationToken.ThrowIfCancellationRequested();
        if (ledger is not DeletionLedgerValidationResult.Verified verified)
        {
            throw Inconsistent();
        }
        var byPhoto = catalogAssets.ToDictionary(a => a.PhotoId);
        var byAsset = catalogAssets.ToDictionary(a => a.AssetId);
        var selected = new List<ApprovedMediaCatalogAsset>();
        long total = 0;
        foreach (var saved in manifestAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (byAsset.TryGetValue(saved.AssetId, out var sameAsset) && sameAsset.PhotoId != saved.PhotoId)
            {
                throw Inconsistent();
            }
            if (verified.DeletedPhotoIds.Contains(saved.PhotoId) || !byPhoto.TryGetValue(saved.PhotoId, out var current))
            {
                continue;
            }
            if (current.AssetId != saved.AssetId || current.Version != saved.Version
                || current.PromotionGeneration != saved.PromotionGeneration
                // Legacy has no persisted variant hash and has no admitted immutable snapshot proof here.
                || current.PromotionGeneration is null)
            {
                throw Inconsistent();
            }
            var variants = current.Variants.ToDictionary(v => v.Kind);
            foreach (var variant in saved.Variants)
            {
                var active = variants[variant.Kind];
                if (active.Key != variant.Key || active.Width != variant.Width || active.Height != variant.Height
                    || active.SizeBytes != variant.SizeBytes || active.ExpectedSha256 != variant.ExpectedSha256)
                {
                    throw Inconsistent();
                }
                total = checked(total + variant.SizeBytes);
            }
            selected.Add(current);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ApprovedMediaRestoreSelectionResult.Selected(expected.EnvironmentId, expected.SnapshotId,
            verified.RestoreEpoch, selected.AsReadOnly(), total);
    }

    private static IReadOnlyList<ApprovedMediaCatalogAsset> FreezeAssets(IReadOnlyList<ApprovedMediaCatalogAsset> assets,
        ApprovedMediaRestoreExpectation expected, bool manifest, CancellationToken cancellationToken, out long totalBytes)
    {
        if (assets is null)
        {
            throw Inconsistent();
        }
        var count = assets.Count;
        if (count < 0 || count > expected.MaximumAssets)
        {
            throw Inconsistent();
        }
        var photos = new HashSet<Guid>();
        var ids = new HashSet<Guid>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ApprovedMediaCatalogAsset>(count);
        totalBytes = 0;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = assets[index];
            if (asset is null || asset.PhotoId == Guid.Empty || asset.AssetId == Guid.Empty || asset.Version < 1
                || asset.PromotionGeneration == Guid.Empty || !photos.Add(asset.PhotoId) || !ids.Add(asset.AssetId)
                || asset.Variants is null || asset.Variants.Count != 3)
            {
                throw Inconsistent();
            }
            var kinds = new HashSet<PhotoVariant>();
            var variants = new List<ApprovedMediaCatalogVariant>(3);
            string? legacyToken = null;
            long assetBytes = 0;
            for (var variantIndex = 0; variantIndex < 3; variantIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var variant = asset.Variants[variantIndex];
                if (variant is null || variant.Kind is not (PhotoVariant.Thumb or PhotoVariant.Card or PhotoVariant.Full)
                    || !kinds.Add(variant.Kind) || variant.Width < 1 || variant.Height < 1
                    || (long)variant.Width * variant.Height > 20_000_000
                    || variant.SizeBytes is < 1 or > MaximumVariantBytes
                    || ObjectKey.Parse(variant.Key.Value) != variant.Key || variant.Key.Area != StorageArea.Derivatives
                    || !keys.Add(variant.Key.Value)
                    || ((manifest || asset.PromotionGeneration is not null) && !IsSha256(variant.ExpectedSha256))
                    || (!manifest && asset.PromotionGeneration is null && variant.ExpectedSha256 is not null))
                {
                    throw Inconsistent();
                }
                if (asset.PromotionGeneration is { } generation)
                {
                    if (variant.Key != ObjectKey.ForPromotedDerivative(generation, variant.Kind))
                    {
                        throw Inconsistent();
                    }
                }
                else
                {
                    if (variant.Key.Value.StartsWith("derivatives/review-", StringComparison.Ordinal))
                    {
                        throw Inconsistent();
                    }
                    var token = variant.Key.Value.Substring("derivatives/".Length, 32);
                    legacyToken ??= token;
                    if (token != legacyToken
                        || variant.Key != ObjectKey.ForDerivative(ObjectKey.Parse("quarantine/" + token), variant.Kind))
                    {
                        throw Inconsistent();
                    }
                }
                variants.Add(variant with { });
                assetBytes = checked(assetBytes + variant.SizeBytes);
            }
            totalBytes = checked(totalBytes + assetBytes);
            if (assetBytes > MaximumAssetBytes || totalBytes > expected.MaximumBytes)
            {
                throw Inconsistent();
            }
            result.Add(asset with { Variants = variants.AsReadOnly() });
        }
        if (assets.Count != count)
        {
            throw Inconsistent();
        }
        return result.AsReadOnly();
    }

    private static IEnumerable<DeletionLedgerEntry> ObserveCancellation(IEnumerable<DeletionLedgerEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool ValidCapture(DateTimeOffset capturedAt, string snapshot) => capturedAt > DateTimeOffset.UnixEpoch
        && capturedAt.Offset == TimeSpan.Zero && snapshot is { Length: > 0 and <= 65_536 } && snapshot.Contains(':')
        && snapshot.All(c => char.IsAsciiDigit(c) || c is ':' or ',');

    private static bool IsSourceDigest(string value) => value is { Length: 71 }
        && value.StartsWith("sha256:", StringComparison.Ordinal) && IsSha256(value[7..]);

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static InvalidDataException Inconsistent() => new("The complete restore selection could not be established.");
}
