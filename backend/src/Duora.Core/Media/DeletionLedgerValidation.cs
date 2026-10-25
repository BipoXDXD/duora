namespace Duora.Core.Media;

public enum DeletionLedgerEntryKind
{
    Prepared = 1,
    SqlCommitted = 2,
    SqlAborting = 3,
    Committed = 4,
    Aborted = 5,
}

/// <summary>Provider-neutral journal metadata, not an authorization credential or proof of physical erasure.</summary>
public sealed record DeletionLedgerEntry(long Sequence, Guid EnvironmentId, Guid WriterEpoch, long FenceGeneration,
    Guid OperationId, Guid PhotoId, byte[] IntentSha256, DeletionLedgerEntryKind Kind, byte[] PreviousHash, byte[] Hash)
{
    public override string ToString() => "DeletionLedgerEntry";
}

public sealed record DeletionLedgerBarrier(Guid EnvironmentId, Guid ClosedWriterEpoch, Guid RestoreEpoch,
    long FenceGeneration, long MaximumSequence, byte[] ChainRoot, string KeyId, byte[] Signature)
{
    public override string ToString() => "DeletionLedgerBarrier";
}

/// <summary>Expected scope from independent custody, never inferred from a restored database or untrusted body.</summary>
public sealed record DeletionLedgerExpectation(Guid EnvironmentId, Guid ClosedWriterEpoch, Guid RestoreEpoch,
    long FenceGeneration, long MinimumSequence, byte[] MinimumChainRoot, int MaximumEntries);

/// <summary>Trusted adapter boundary: authenticate every barrier field with independently pinned verification keys.</summary>
public interface IDeletionLedgerBarrierAuthenticator
{
    bool Verify(DeletionLedgerBarrier barrier);
}

public abstract record DeletionLedgerValidationResult
{
    public sealed record Verified(Guid EnvironmentId, Guid RestoreEpoch, long MaximumSequence,
        IReadOnlySet<Guid> DeletedPhotoIds) : DeletionLedgerValidationResult
    {
        public override string ToString() => "VerifiedDeletionLedgerBarrier";
    }

    public sealed record Refused : DeletionLedgerValidationResult;
}

/// <summary>Regression scaffold only; refuses every journal. No provider, I/O, registration or restore admission.</summary>
public static class DeletionLedgerValidation
{
    public static DeletionLedgerValidationResult Validate(DeletionLedgerExpectation expected,
        DeletionLedgerBarrier barrier, IEnumerable<DeletionLedgerEntry> entries, IDeletionLedgerBarrierAuthenticator? authenticator) =>
        new DeletionLedgerValidationResult.Refused();
}
