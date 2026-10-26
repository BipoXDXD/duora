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
    string SourceDigest, int MaximumAssets, long MaximumBytes);

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
    internal static ApprovedMediaRestoreSelectionResult Select(ApprovedMediaCatalog catalog,
        ApprovedMediaRestoreManifest manifest, ApprovedMediaRestoreExpectation expected,
        DeletionLedgerExpectation ledgerExpected, DeletionLedgerBarrier barrier,
        IEnumerable<DeletionLedgerEntry> entries, IApprovedMediaRestoreManifestAuthenticator? manifestAuthenticator,
        IDeletionLedgerBarrierAuthenticator? ledgerAuthenticator, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Test-first contract. No source can be selected until the complete join is implemented and qualified.
        return new ApprovedMediaRestoreSelectionResult.Refused();
    }
}
