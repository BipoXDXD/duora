using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class DeletionLedgerBoundaryFailureTests
{
    [Fact]
    public void AuthenticationFailureRefusesTheWholeJournal()
    {
        var (expected, barrier, entries) = Journal();
        var authenticator = new Authenticator(fail: true);
        Assert.IsType<DeletionLedgerValidationResult.Refused>(DeletionLedgerValidation.Validate(expected, barrier, entries, authenticator));
        Assert.True(authenticator.Fired);
    }

    [Theory]
    [InlineData("get-enumerator")]
    [InlineData("move-next")]
    [InlineData("after-entry")]
    [InlineData("current")]
    [InlineData("dispose")]
    public void EnumerationFailureIncludingDisposalCannotReleaseAPartialDeletionSet(string failure)
    {
        var (expected, barrier, entries) = Journal();
        var source = new FailedEnumeration(entries, failure);
        Assert.IsType<DeletionLedgerValidationResult.Refused>(DeletionLedgerValidation.Validate(expected, barrier, source, new Authenticator(false)));
        Assert.True(source.Fired);
    }

    private static (DeletionLedgerExpectation Expected, DeletionLedgerBarrier Barrier, DeletionLedgerEntry[] Entries) Journal()
    {
        var environment = Guid.NewGuid();
        var writer = Guid.NewGuid();
        var restore = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var photo = Guid.NewGuid();
        var intent = SHA256.HashData("synthetic-delete-intent"u8);
        var previous = new byte[32];
        var entries = new List<DeletionLedgerEntry>();
        foreach (var kind in new[] { DeletionLedgerEntryKind.Prepared, DeletionLedgerEntryKind.SqlCommitted, DeletionLedgerEntryKind.Committed })
        {
            var sequence = entries.Count + 1;
            var canonical = string.Join('\n', new[]
            {
                "DUORA-DELETE-LEDGER-V1", sequence.ToString(CultureInfo.InvariantCulture), environment.ToString("D"), writer.ToString("D"),
                "1", operation.ToString("D"), photo.ToString("D"), Convert.ToHexString(intent),
                ((int)kind).ToString(CultureInfo.InvariantCulture), Convert.ToHexString(previous),
            }) + "\n";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            entries.Add(new(sequence, environment, writer, 1, operation, photo, intent, kind, previous, hash));
            previous = hash;
        }
        return (new(environment, writer, restore, 1, 0, new byte[32], 10),
            new(environment, writer, restore, 1, 3, previous, "synthetic", [1]), [.. entries]);
    }

    private sealed class Authenticator(bool fail) : IDeletionLedgerBarrierAuthenticator
    {
        internal bool Fired { get; private set; }
        public bool Verify(DeletionLedgerBarrier barrier)
        {
            if (fail)
            {
                Fired = true;
                throw new IOException("Synthetic authenticator failure.");
            }
            return true;
        }
    }

    private sealed class FailedEnumeration(DeletionLedgerEntry[] entries, string failure) : IEnumerable<DeletionLedgerEntry>
    {
        internal bool Fired { get; private set; }
        private void Fail(string boundary)
        {
            if (failure == boundary)
            {
                Fired = true;
                throw new IOException("Synthetic enumeration boundary failure.");
            }
        }
        public IEnumerator<DeletionLedgerEntry> GetEnumerator()
        {
            Fail("get-enumerator");
            return new Enumerator(this, entries);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(FailedEnumeration owner, DeletionLedgerEntry[] entries) : IEnumerator<DeletionLedgerEntry>
        {
            private int _index = -1;
            public DeletionLedgerEntry Current
            {
                get
                {
                    owner.Fail("current");
                    return entries[_index];
                }
            }
            object System.Collections.IEnumerator.Current => Current;
            public bool MoveNext()
            {
                owner.Fail(_index < 0 ? "move-next" : "after-entry");
                return ++_index < entries.Length;
            }
            public void Reset() => throw new NotSupportedException();
            public void Dispose() => owner.Fail("dispose");
        }
    }
}
