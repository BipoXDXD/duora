using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Barrier = Duora.ClosureProbe.Barrier;

namespace Duora.ClosureBoundary;

internal enum JournalState { Reserved, Open, Closing, Closed }
internal sealed record JournalEntry(Guid Domain, long Epoch, int ProtocolVersion, Guid Generation,
    string Area, string State, long WitnessRevision);
internal sealed record JournalRecord(long Seq, string Prev, string Payload, string Hash);
internal sealed record GenerationState(JournalState State, long WitnessRevision);
internal sealed record JournalSnapshot(long Sequence, string Hash, Dictionary<Guid, GenerationState> Generations);

/// <summary>Checks integrity, not authenticity against the trusted authority or a complete rollback.</summary>
[SupportedOSPlatform("linux")]
internal sealed class ControlJournal(ProtectedLayout layout, bool witness, Barrier barrier)
{
    internal JournalSnapshot Read()
    {
        using var file = new FileStream(layout.OpenControl("domain.journal"), FileAccess.ReadWrite);
        if (file.Length is 0 or > 1048576)
        {
            throw new InvalidDataException("Invalid journal length.");
        }

        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (bytes[^1] != (byte)'\n')
        {
            throw new InvalidDataException("Torn journal tail.");
        }

        var snapshot = new JournalSnapshot(0, new string('0', 64), []);
        var lines = Encoding.UTF8.GetString(bytes).Split('\n');
        foreach (var line in lines[..^1])
        {
            if (line.Length is 0 or > 8192)
            {
                throw new InvalidDataException("Invalid journal record length.");
            }

            var record = JsonSerializer.Deserialize<JournalRecord>(line, Wire.Options)
                ?? throw new InvalidDataException("Invalid journal record.");
            if (record.Seq != snapshot.Sequence + 1 || record.Prev != snapshot.Hash
                || record.Hash != Hash(record.Seq, record.Prev, record.Payload))
            {
                throw new InvalidDataException("Journal chain mismatch.");
            }

            var payload = Convert.FromBase64String(record.Payload);
            var entry = JsonSerializer.Deserialize<JournalEntry>(payload, Wire.Options)
                ?? throw new InvalidDataException("Empty journal entry.");
            Apply(snapshot, entry, record.Seq);
            snapshot = snapshot with { Sequence = record.Seq, Hash = record.Hash };
        }

        if (snapshot.Sequence == 0)
        {
            throw new InvalidDataException("Missing genesis.");
        }

        return snapshot;
    }

    internal long AppendAndSync(GenerationRegistration registration, JournalState state, long witnessRevision)
    {
        var snapshot = Read();
        var sequence = snapshot.Sequence + 1;
        var revision = witness ? sequence : witnessRevision;
        var entry = new JournalEntry(layout.Enrollment.Domain, layout.Enrollment.Epoch, 2,
            registration.Generation, registration.Area, state.ToString(), revision);
        Apply(snapshot, entry, sequence);
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(entry, Wire.Options));
        var record = new JournalRecord(sequence, snapshot.Hash, payload, Hash(sequence, snapshot.Hash, payload));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Wire.Options) + "\n");
        using (var file = new FileStream(layout.OpenControl("domain.journal"), FileAccess.ReadWrite))
        {
            file.Seek(0, SeekOrigin.End);
            file.Write(bytes);
            barrier.Wait(state == JournalState.Closing ? "closing-sync" : "closed-sync");
            file.Flush(flushToDisk: true);
        }

        barrier.Wait(witness ? "witness-synced" : state == JournalState.Closing ? "closing-synced" : "closed-synced");
        return revision;
    }

    // A previous attempt may have written a valid record but failed before its sync/ACK.
    // Reading that record back from the page cache is not a durability acknowledgement.
    internal void Synchronize()
    {
        using var file = layout.OpenControl("domain.journal");
        barrier.Wait("recovery-sync");
        LinuxBoundary.Sync(file);
    }

    private void Apply(JournalSnapshot snapshot, JournalEntry entry, long sequence)
    {
        if (entry.Domain != layout.Enrollment.Domain || entry.Epoch != layout.Enrollment.Epoch || entry.ProtocolVersion != 2)
        {
            throw new InvalidDataException("Journal domain mismatch.");
        }

        if (sequence == 1)
        {
            if (entry.State != "Genesis" || entry.Generation != Guid.Empty || entry.Area != "" || entry.WitnessRevision != 0)
            {
                throw new InvalidDataException("Invalid genesis.");
            }

            return;
        }

        var registration = layout.Enrollment.Generations.SingleOrDefault(g => g.Generation == entry.Generation && g.Area == entry.Area);
        if (registration is null || !Enum.TryParse<JournalState>(entry.State, out var state)
            || !Enum.IsDefined(state) || state.ToString() != entry.State)
        {
            throw new InvalidDataException("Unknown journal generation or state.");
        }

        snapshot.Generations.TryGetValue(entry.Generation, out var previous);
        var allowed = witness
            ? state == JournalState.Closed && previous is null && entry.WitnessRevision == sequence
            : state switch
            {
                JournalState.Reserved => previous is null && entry.WitnessRevision == 0,
                JournalState.Open => previous?.State == JournalState.Reserved && entry.WitnessRevision == 0,
                JournalState.Closing => previous?.State == JournalState.Open && entry.WitnessRevision == 0,
                JournalState.Closed => previous?.State == JournalState.Closing && entry.WitnessRevision > 0,
                _ => false,
            };
        if (!allowed)
        {
            throw new InvalidDataException("Nonmonotonic journal transition.");
        }

        snapshot.Generations[entry.Generation] = new GenerationState(state, entry.WitnessRevision);
    }

    private static string Hash(long sequence, string previous, string payload) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture) + "\n" + previous + "\n" + payload)));
}
