using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class DeletionLedgerValidationTests
{
    private static readonly Guid EnvironmentId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid WriterEpoch = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid RestoreEpoch = Guid.Parse("10000000-0000-4000-8000-000000000003");
    private static readonly Guid OperationA = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid OperationB = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid PhotoA = Guid.Parse("30000000-0000-4000-8000-000000000001");
    private static readonly Guid PhotoB = Guid.Parse("30000000-0000-4000-8000-000000000002");
    private static readonly Guid PhotoC = Guid.Parse("30000000-0000-4000-8000-000000000003");

    [Fact]
    public void VerifiedCommitProducesALogicalPhotoTombstoneOnlyAfterTheWholeJournal()
    {
        var fixture = Committed();
        var result = Assert.IsType<DeletionLedgerValidationResult.Verified>(Validate(fixture));
        Assert.Equal(EnvironmentId, result.EnvironmentId);
        Assert.Equal(RestoreEpoch, result.RestoreEpoch);
        Assert.Equal(3, result.MaximumSequence);
        Assert.Equal(PhotoA, Assert.Single(result.DeletedPhotoIds));
    }

    [Fact]
    public void AnAuthenticatedEmptyFirstJournalHasNoDeletionEffects()
    {
        var result = Assert.IsType<DeletionLedgerValidationResult.Verified>(Validate(Journal([])));
        Assert.Equal(0, result.MaximumSequence);
        Assert.Empty(result.DeletedPhotoIds);
    }

    [Fact]
    public void InterleavedOperationsKeepCommittedAndAbortedIntentsSeparate()
    {
        var fixture = Journal([
            new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.Prepared),
            new(OperationA, PhotoA, DeletionLedgerEntryKind.SqlCommitted),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.SqlAborting),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.Aborted),
            new(OperationA, PhotoA, DeletionLedgerEntryKind.Committed),
        ]);
        var result = Assert.IsType<DeletionLedgerValidationResult.Verified>(Validate(fixture));
        Assert.Equal(PhotoA, Assert.Single(result.DeletedPhotoIds));
    }

    [Fact]
    public void TwoConfirmedDeletionsOfTheSameLogicalPhotoDoNotBecomeGenerationSpecific()
    {
        var fixture = Journal([
            new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared),
            new(OperationA, PhotoA, DeletionLedgerEntryKind.SqlCommitted),
            new(OperationA, PhotoA, DeletionLedgerEntryKind.Committed),
            new(OperationB, PhotoA, DeletionLedgerEntryKind.Prepared),
            new(OperationB, PhotoA, DeletionLedgerEntryKind.SqlCommitted),
            new(OperationB, PhotoA, DeletionLedgerEntryKind.Committed),
        ]);
        var result = Assert.IsType<DeletionLedgerValidationResult.Verified>(Validate(fixture));
        Assert.Equal(PhotoA, Assert.Single(result.DeletedPhotoIds));
    }

    [Fact]
    public void AnAbortedOperationDoesNotInventADeletion()
    {
        var fixture = Journal([
            new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared),
            new(OperationA, PhotoA, DeletionLedgerEntryKind.SqlAborting),
            new(OperationA, PhotoA, DeletionLedgerEntryKind.Aborted),
        ]);
        Assert.Empty(Assert.IsType<DeletionLedgerValidationResult.Verified>(Validate(fixture)).DeletedPhotoIds);
    }

    [Fact]
    public void EarlierClosedEpochDeletionsRemainInTheAuthenticatedHistory()
    {
        var fixture = Journal([
            .. CommitEvents(),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.Prepared),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.SqlCommitted),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.Committed),
        ]);
        var entries = fixture.Entries.ToArray();
        var oldEpoch = Guid.Parse("10000000-0000-4000-8000-000000000004");
        for (var index = 0; index < 3; index++)
        {
            entries[index] = entries[index] with { WriterEpoch = oldEpoch, FenceGeneration = 6 };
        }

        entries = Rehash(entries);
        fixture = fixture with { Entries = entries, Barrier = fixture.Barrier with { ChainRoot = entries[^1].Hash } };
        var result = Assert.IsType<DeletionLedgerValidationResult.Verified>(Validate(fixture));
        Assert.Equal(2, result.DeletedPhotoIds.Count);
        Assert.Contains(PhotoA, result.DeletedPhotoIds);
        Assert.Contains(PhotoB, result.DeletedPhotoIds);
    }

    [Fact]
    public void AJournalCannotResumeAnOlderFencingGenerationAfterTheCurrentGeneration()
    {
        var fixture = Journal([
            .. CommitEvents(),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.Prepared),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.SqlCommitted),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.Committed),
        ]);
        var entries = fixture.Entries.ToArray();
        var oldEpoch = Guid.Parse("10000000-0000-4000-8000-000000000004");
        for (var index = 3; index < entries.Length; index++)
        {
            entries[index] = entries[index] with { WriterEpoch = oldEpoch, FenceGeneration = 6 };
        }

        entries = Rehash(entries);
        Refused(fixture with { Entries = entries, Barrier = fixture.Barrier with { ChainRoot = entries[^1].Hash } });
    }

    [Fact]
    public void ANewTailMustExtendTheExactIndependentlyPinnedCheckpoint()
    {
        var fixture = Journal([
            .. CommitEvents(),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.Prepared),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.SqlCommitted),
            new(OperationB, PhotoB, DeletionLedgerEntryKind.Committed),
        ]);
        fixture = fixture with { Expected = fixture.Expected with { MinimumSequence = 3, MinimumChainRoot = fixture.Entries[2].Hash } };
        var result = Assert.IsType<DeletionLedgerValidationResult.Verified>(Validate(fixture));
        Assert.Equal(6, result.MaximumSequence);
        Assert.Contains(PhotoA, result.DeletedPhotoIds);
        Assert.Contains(PhotoB, result.DeletedPhotoIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAuthenticSameHeightOrLongerForkCannotReplacePinnedDeletionHistory(bool longerFork)
    {
        var trusted = Committed();
        List<Event> fork =
        [
            new(OperationA, PhotoB, DeletionLedgerEntryKind.Prepared),
            new(OperationA, PhotoB, DeletionLedgerEntryKind.SqlCommitted),
            new(OperationA, PhotoB, DeletionLedgerEntryKind.Committed),
        ];
        if (longerFork)
        {
            fork.AddRange([
                new(OperationB, PhotoC, DeletionLedgerEntryKind.Prepared),
                new(OperationB, PhotoC, DeletionLedgerEntryKind.SqlCommitted),
                new(OperationB, PhotoC, DeletionLedgerEntryKind.Committed),
            ]);
        }

        var fixture = Journal([.. fork]);
        fixture = fixture with { Expected = fixture.Expected with { MinimumSequence = 3, MinimumChainRoot = trusted.Entries[2].Hash } };
        Refused(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnverifiedOrUnconfiguredBarrierCannotAdmitRestore(bool missingAuthenticator)
    {
        var fixture = Committed();
        Assert.IsType<DeletionLedgerValidationResult.Refused>(DeletionLedgerValidation.Validate(
            fixture.Expected, fixture.Barrier, fixture.Entries, missingAuthenticator ? null : new SyntheticAuthenticator(false)));
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("closed-epoch")]
    [InlineData("restore-epoch")]
    [InlineData("fence")]
    [InlineData("checkpoint")]
    [InlineData("limit")]
    public void EvenAnAuthenticJournalMustMatchTheIndependentRestoreExpectation(string mismatch)
    {
        var fixture = Committed();
        var expected = mismatch switch
        {
            "environment" => fixture.Expected with { EnvironmentId = Guid.NewGuid() },
            "closed-epoch" => fixture.Expected with { ClosedWriterEpoch = Guid.NewGuid() },
            "restore-epoch" => fixture.Expected with { RestoreEpoch = Guid.NewGuid() },
            "fence" => fixture.Expected with { FenceGeneration = 8 },
            "checkpoint" => fixture.Expected with { MinimumSequence = 4 },
            _ => fixture.Expected with { MaximumEntries = 2 },
        };
        Refused(fixture with { Expected = expected });
    }

    [Fact]
    public void ABarrierAboveTheResourceLimitIsRefusedBeforeConsumingAnyEntry()
    {
        var fixture = Committed();
        var entries = new BudgetedEntries(fixture.Entries, 0);
        Assert.IsType<DeletionLedgerValidationResult.Refused>(DeletionLedgerValidation.Validate(
            fixture.Expected with { MaximumEntries = 2 }, fixture.Barrier, entries, new SyntheticAuthenticator(true)));
        Assert.Equal(0, entries.Reads);
        Assert.Equal(0, entries.Attempts);
    }

    [Fact]
    public void AnUnderstatedBarrierDoesNotCauseUnboundedEnumerationBeforeRefusal()
    {
        var fixture = Committed();
        var entries = new BudgetedEntries(fixture.Entries, 2);
        var barrier = fixture.Barrier with { MaximumSequence = 1, ChainRoot = fixture.Entries[0].Hash };
        Assert.IsType<DeletionLedgerValidationResult.Refused>(DeletionLedgerValidation.Validate(
            fixture.Expected with { MaximumEntries = 2 }, barrier, entries, new SyntheticAuthenticator(true)));
        Assert.InRange(entries.Reads, 0, 2);
        Assert.InRange(entries.Attempts, 0, 2);
    }

    [Theory]
    [InlineData("same-epoch")]
    [InlineData("empty-environment")]
    [InlineData("empty-writer")]
    [InlineData("empty-restore")]
    [InlineData("zero-fence")]
    public void MatchingButInvalidScopeValuesAreNotAuthority(string invalid)
    {
        var fixture = Committed();
        var barrier = invalid switch
        {
            "same-epoch" => fixture.Barrier with { RestoreEpoch = WriterEpoch },
            "empty-environment" => fixture.Barrier with { EnvironmentId = Guid.Empty },
            "empty-writer" => fixture.Barrier with { ClosedWriterEpoch = Guid.Empty },
            "empty-restore" => fixture.Barrier with { RestoreEpoch = Guid.Empty },
            _ => fixture.Barrier with { FenceGeneration = 0 },
        };
        var expected = fixture.Expected with
        {
            EnvironmentId = barrier.EnvironmentId,
            ClosedWriterEpoch = barrier.ClosedWriterEpoch,
            RestoreEpoch = barrier.RestoreEpoch,
            FenceGeneration = barrier.FenceGeneration,
        };
        Refused(fixture with { Expected = expected, Barrier = barrier });
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("hash")]
    [InlineData("root")]
    [InlineData("previous")]
    [InlineData("short-digest")]
    public void ChainTamperingCannotProduceAnyPartialDeletionSet(string tamper)
    {
        var fixture = Committed();
        var entries = fixture.Entries.ToArray();
        if (tamper == "root")
        {
            Refused(fixture with { Barrier = fixture.Barrier with { ChainRoot = new byte[32] } });
            return;
        }

        entries[1] = tamper switch
        {
            "payload" => entries[1] with { PhotoId = PhotoB },
            "hash" => entries[1] with { Hash = new byte[32] },
            "previous" => entries[1] with { PreviousHash = new byte[32] },
            _ => entries[1] with { IntentSha256 = new byte[31] },
        };
        Refused(fixture with { Entries = entries });
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("duplicate")]
    [InlineData("missing-tail")]
    [InlineData("extra-tail")]
    public void AConsistentPrefixDoesNotReplaceTheAdvertisedCompleteBarrier(string defect)
    {
        var fixture = Committed();
        if (defect == "gap")
        {
            fixture = Journal(CommitEvents(), [1, 3, 4]);
        }
        else if (defect == "duplicate")
        {
            fixture = Journal(CommitEvents(), [1, 1, 2]);
        }
        else if (defect == "missing-tail")
        {
            fixture = fixture with { Entries = fixture.Entries.Take(2).ToArray() };
        }
        else
        {
            fixture = fixture with { Barrier = fixture.Barrier with { MaximumSequence = 2, ChainRoot = fixture.Entries[1].Hash } };
        }

        Refused(fixture);
    }

    [Theory]
    [InlineData(DeletionLedgerEntryKind.Prepared)]
    [InlineData(DeletionLedgerEntryKind.SqlCommitted)]
    [InlineData(DeletionLedgerEntryKind.SqlAborting)]
    public void AnyUnresolvedOperationBlocksRestoreEvenWhenOtherDeletesAreComplete(DeletionLedgerEntryKind pending)
    {
        List<Event> events = [.. CommitEvents(), new(OperationB, PhotoB, DeletionLedgerEntryKind.Prepared)];
        if (pending != DeletionLedgerEntryKind.Prepared)
        {
            events.Add(new(OperationB, PhotoB, pending));
        }

        Refused(Journal([.. events]));
    }

    [Theory]
    [InlineData("commit-no-witness")]
    [InlineData("abort-no-witness")]
    [InlineData("no-prepared")]
    [InlineData("duplicate-prepared")]
    [InlineData("fork")]
    [InlineData("unknown-kind")]
    [InlineData("changed-photo")]
    [InlineData("changed-intent")]
    public void AuthenticatedStateMachineViolationsCannotBeHealedFromRestoredDatabaseAbsence(string defect)
    {
        Event[] events = defect switch
        {
            "commit-no-witness" => [new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared), new(OperationA, PhotoA, DeletionLedgerEntryKind.Committed)],
            "abort-no-witness" => [new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared), new(OperationA, PhotoA, DeletionLedgerEntryKind.Aborted)],
            "no-prepared" => [new(OperationA, PhotoA, DeletionLedgerEntryKind.SqlCommitted), new(OperationA, PhotoA, DeletionLedgerEntryKind.Committed)],
            "duplicate-prepared" => [new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared), .. CommitEvents()],
            "fork" => [new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared), new(OperationA, PhotoA, DeletionLedgerEntryKind.SqlCommitted), new(OperationA, PhotoA, DeletionLedgerEntryKind.SqlAborting), new(OperationA, PhotoA, DeletionLedgerEntryKind.Aborted)],
            "unknown-kind" => [new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared), new(OperationA, PhotoA, (DeletionLedgerEntryKind)999)],
            "changed-photo" => [new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared), new(OperationA, PhotoB, DeletionLedgerEntryKind.SqlCommitted), new(OperationA, PhotoB, DeletionLedgerEntryKind.Committed)],
            _ => [new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared), new(OperationA, PhotoA, DeletionLedgerEntryKind.SqlCommitted, new byte[32]), new(OperationA, PhotoA, DeletionLedgerEntryKind.Committed, new byte[32])],
        };
        Refused(Journal(events));
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("fence")]
    [InlineData("environment")]
    public void JournalEntriesCannotSwitchScopeInsideAnOtherwiseValidBarrier(string scope)
    {
        var fixture = Committed();
        var entries = fixture.Entries.ToArray();
        if (scope == "environment")
        {
            var otherEnvironment = Guid.NewGuid();
            for (var index = 0; index < entries.Length; index++)
            {
                entries[index] = entries[index] with { EnvironmentId = otherEnvironment };
            }
        }
        else
        {
            entries[0] = scope == "epoch" ? entries[0] with { WriterEpoch = Guid.NewGuid() } : entries[0] with { FenceGeneration = 6 };
        }

        entries = Rehash(entries);
        Refused(fixture with { Entries = entries, Barrier = fixture.Barrier with { ChainRoot = entries[^1].Hash } });
    }

    private static Fixture Committed() => Journal(CommitEvents());

    private static Event[] CommitEvents() =>
    [
        new(OperationA, PhotoA, DeletionLedgerEntryKind.Prepared),
        new(OperationA, PhotoA, DeletionLedgerEntryKind.SqlCommitted),
        new(OperationA, PhotoA, DeletionLedgerEntryKind.Committed),
    ];

    private static DeletionLedgerValidationResult Validate(Fixture fixture) => DeletionLedgerValidation.Validate(
        fixture.Expected, fixture.Barrier, fixture.Entries, new SyntheticAuthenticator(true));

    private static void Refused(Fixture fixture) => Assert.IsType<DeletionLedgerValidationResult.Refused>(Validate(fixture));

    private static Fixture Journal(Event[] events, long[]? sequences = null)
    {
        var entries = events.Select((item, index) => new DeletionLedgerEntry(sequences?[index] ?? index + 1,
            EnvironmentId, WriterEpoch, 7, item.OperationId, item.PhotoId,
            item.Intent ?? SHA256.HashData(Encoding.ASCII.GetBytes("delete-photo:" + item.PhotoId.ToString("D"))),
            item.Kind, [], [])).ToArray();
        entries = Rehash(entries);
        var barrier = new DeletionLedgerBarrier(EnvironmentId, WriterEpoch, RestoreEpoch, 7,
            entries.Length == 0 ? 0 : entries[^1].Sequence, entries.Length == 0 ? new byte[32] : entries[^1].Hash,
            "synthetic-test-key", [1, 2, 3]);
        return new(new(EnvironmentId, WriterEpoch, RestoreEpoch, 7, 0, new byte[32], 100), barrier, entries);
    }

    private static DeletionLedgerEntry[] Rehash(DeletionLedgerEntry[] entries)
    {
        var result = new DeletionLedgerEntry[entries.Length];
        var previous = new byte[32];
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index] with { PreviousHash = previous };
            // Independent fixture encoding of the written v1 contract; never call the product hash helper.
            var body = string.Join('\n', new[]
            {
                "DUORA-DELETE-LEDGER-V1", entry.Sequence.ToString(CultureInfo.InvariantCulture),
                entry.EnvironmentId.ToString("D"), entry.WriterEpoch.ToString("D"),
                entry.FenceGeneration.ToString(CultureInfo.InvariantCulture), entry.OperationId.ToString("D"),
                entry.PhotoId.ToString("D"), Convert.ToHexString(entry.IntentSha256),
                ((int)entry.Kind).ToString(CultureInfo.InvariantCulture), Convert.ToHexString(previous),
            }) + "\n";
            previous = SHA256.HashData(Encoding.UTF8.GetBytes(body));
            result[index] = entry with { Hash = previous };
        }

        return result;
    }

    private sealed record Event(Guid OperationId, Guid PhotoId, DeletionLedgerEntryKind Kind, byte[]? Intent = null);

    private sealed record Fixture(DeletionLedgerExpectation Expected, DeletionLedgerBarrier Barrier, DeletionLedgerEntry[] Entries);

    private sealed class BudgetedEntries(IReadOnlyList<DeletionLedgerEntry> entries, int maximumReads) : IEnumerable<DeletionLedgerEntry>
    {
        internal int Reads { get; private set; }
        internal int Attempts { get; private set; }

        public IEnumerator<DeletionLedgerEntry> GetEnumerator()
        {
            foreach (var entry in entries)
            {
                Attempts++;
                if (Reads >= maximumReads)
                {
                    throw new InvalidOperationException("Validator enumerated beyond the bounded fixture budget.");
                }

                Reads++;
                yield return entry;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Explicitly synthetic trust decision; no signature/key/provider/custody has been qualified by these tests.
    private sealed class SyntheticAuthenticator(bool accepted) : IDeletionLedgerBarrierAuthenticator
    {
        public bool Verify(DeletionLedgerBarrier barrier) => accepted;
    }
}
