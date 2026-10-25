using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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

/// <summary>Bounded pure validation of a complete authenticated journal; no provider, I/O or restore admission.</summary>
public static class DeletionLedgerValidation
{
    public static DeletionLedgerValidationResult Validate(DeletionLedgerExpectation expected,
        DeletionLedgerBarrier barrier, IEnumerable<DeletionLedgerEntry> entries, IDeletionLedgerBarrierAuthenticator? authenticator)
    {
        try
        {
            return ValidateCore(expected, barrier, entries, authenticator);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Authentication/enumeration/disposal failures never release a partial deletion set.
            return new DeletionLedgerValidationResult.Refused();
        }
    }

    private static DeletionLedgerValidationResult ValidateCore(DeletionLedgerExpectation expected,
        DeletionLedgerBarrier barrier, IEnumerable<DeletionLedgerEntry> entries, IDeletionLedgerBarrierAuthenticator? authenticator)
    {
        var refused = new DeletionLedgerValidationResult.Refused();
        if (expected is null || barrier is null || entries is null || authenticator is null
            || expected.EnvironmentId == Guid.Empty || expected.ClosedWriterEpoch == Guid.Empty
            || expected.RestoreEpoch == Guid.Empty || expected.RestoreEpoch == expected.ClosedWriterEpoch
            || expected.FenceGeneration <= 0 || expected.MinimumSequence < 0 || expected.MaximumEntries <= 0
            || expected.MinimumChainRoot is not { Length: 32 } || barrier.ChainRoot is not { Length: 32 }
            || barrier.Signature is not { Length: > 0 } || string.IsNullOrWhiteSpace(barrier.KeyId)
            || barrier.EnvironmentId != expected.EnvironmentId || barrier.ClosedWriterEpoch != expected.ClosedWriterEpoch
            || barrier.RestoreEpoch != expected.RestoreEpoch || barrier.FenceGeneration != expected.FenceGeneration
            || barrier.MaximumSequence < expected.MinimumSequence || barrier.MaximumSequence > expected.MaximumEntries
            || barrier.MaximumSequence < 0)
        {
            return refused;
        }

        // Caller-owned arrays cannot change the checkpoint after authentication or during enumeration.
        barrier = barrier with { ChainRoot = [.. barrier.ChainRoot], Signature = [.. barrier.Signature] };
        var minimumRoot = expected.MinimumChainRoot.ToArray();
        if (!authenticator.Verify(barrier))
        {
            return refused;
        }
        var previous = new byte[32];
        if (expected.MinimumSequence == 0 && !CryptographicOperations.FixedTimeEquals(minimumRoot, previous))
        {
            return refused;
        }
        var operations = new Dictionary<Guid, Operation>();
        var epochs = new Dictionary<long, Guid>();
        var deleted = new HashSet<Guid>();
        long fence = 0;
        using var iterator = entries.GetEnumerator();
        for (long sequence = 1; sequence <= barrier.MaximumSequence; sequence++)
        {
            if (!iterator.MoveNext())
            {
                return refused;
            }
            var entry = iterator.Current;
            if (entry is null || entry.Sequence != sequence || entry.EnvironmentId != expected.EnvironmentId
                || entry.WriterEpoch == Guid.Empty || entry.WriterEpoch == expected.RestoreEpoch
                || entry.FenceGeneration < fence || entry.FenceGeneration <= 0 || entry.FenceGeneration > expected.FenceGeneration
                || (entry.FenceGeneration == expected.FenceGeneration && entry.WriterEpoch != expected.ClosedWriterEpoch)
                || entry.OperationId == Guid.Empty || entry.PhotoId == Guid.Empty
                || entry.IntentSha256 is not { Length: 32 } || entry.PreviousHash is not { Length: 32 }
                || entry.Hash is not { Length: 32 } || !CryptographicOperations.FixedTimeEquals(entry.PreviousHash, previous))
            {
                return refused;
            }
            entry = entry with { IntentSha256 = [.. entry.IntentSha256], Hash = [.. entry.Hash], PreviousHash = [.. entry.PreviousHash] };
            if (epochs.TryGetValue(entry.FenceGeneration, out var epoch) && epoch != entry.WriterEpoch)
            {
                return refused;
            }
            epochs[entry.FenceGeneration] = entry.WriterEpoch;
            fence = entry.FenceGeneration;
            if (!CryptographicOperations.FixedTimeEquals(Hash(entry), entry.Hash)
                || (sequence == expected.MinimumSequence && !CryptographicOperations.FixedTimeEquals(entry.Hash, minimumRoot)))
            {
                return refused;
            }
            previous = entry.Hash;
            if (!operations.TryGetValue(entry.OperationId, out var operation))
            {
                if (entry.Kind != DeletionLedgerEntryKind.Prepared)
                {
                    return refused;
                }
                operations.Add(entry.OperationId, new(entry.PhotoId, entry.WriterEpoch, entry.FenceGeneration, entry.IntentSha256, entry.Kind));
                continue;
            }
            if (operation.PhotoId != entry.PhotoId || operation.WriterEpoch != entry.WriterEpoch
                || operation.FenceGeneration != entry.FenceGeneration
                || !CryptographicOperations.FixedTimeEquals(operation.Intent, entry.IntentSha256)
                || !Transition(operation.Kind, entry.Kind))
            {
                return refused;
            }
            operations[entry.OperationId] = operation with { Kind = entry.Kind };
            if (entry.Kind == DeletionLedgerEntryKind.Committed)
            {
                deleted.Add(entry.PhotoId);
            }
        }
        // One lookahead detects a withheld tail without materializing an unbounded enumerable.
        if (iterator.MoveNext() || !CryptographicOperations.FixedTimeEquals(previous, barrier.ChainRoot)
            || operations.Values.Any(operation => operation.Kind is not (DeletionLedgerEntryKind.Committed or DeletionLedgerEntryKind.Aborted)))
        {
            return refused;
        }
        return new DeletionLedgerValidationResult.Verified(expected.EnvironmentId, expected.RestoreEpoch,
            barrier.MaximumSequence, deleted.ToFrozenSet());
    }

    private static bool Transition(DeletionLedgerEntryKind before, DeletionLedgerEntryKind after) => (before, after) is
        (DeletionLedgerEntryKind.Prepared, DeletionLedgerEntryKind.SqlCommitted)
        or (DeletionLedgerEntryKind.Prepared, DeletionLedgerEntryKind.SqlAborting)
        or (DeletionLedgerEntryKind.SqlCommitted, DeletionLedgerEntryKind.Committed)
        or (DeletionLedgerEntryKind.SqlAborting, DeletionLedgerEntryKind.Aborted);

    private static byte[] Hash(DeletionLedgerEntry entry)
    {
        var canonical = string.Join('\n', new[]
        {
            "DUORA-DELETE-LEDGER-V1", entry.Sequence.ToString(CultureInfo.InvariantCulture),
            entry.EnvironmentId.ToString("D"), entry.WriterEpoch.ToString("D"),
            entry.FenceGeneration.ToString(CultureInfo.InvariantCulture), entry.OperationId.ToString("D"),
            entry.PhotoId.ToString("D"), Convert.ToHexString(entry.IntentSha256),
            ((int)entry.Kind).ToString(CultureInfo.InvariantCulture), Convert.ToHexString(entry.PreviousHash),
        }) + "\n";
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private sealed record Operation(Guid PhotoId, Guid WriterEpoch, long FenceGeneration, byte[] Intent, DeletionLedgerEntryKind Kind);
}
