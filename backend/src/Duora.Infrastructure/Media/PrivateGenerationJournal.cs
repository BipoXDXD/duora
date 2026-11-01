using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

internal sealed record PrivateGenerationJournalEntry(PrivateGenerationIdentity Identity, string State, long Revision);

[SupportedOSPlatform("linux")]
internal sealed class PrivateGenerationJournal(PrivateGenerationLayout layout)
{
    private string Genesis => $"duora-private-journal-v1\n{layout.Enrollment.Domain.Id:N}\n{layout.Enrollment.Incarnation:N}\n";

    private (string Hash, Dictionary<PrivateGenerationIdentity, PrivateGenerationJournalEntry> Entries) Read()
    {
        using var file = new FileStream(layout.OpenControl("domain.journal"), FileAccess.ReadWrite);
        if (file.Length < Genesis.Length || file.Length > 67_108_864)
        {
            throw new InvalidDataException("Storage journal requires custody recovery or rotation.");
        }
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        if (!text.StartsWith(Genesis, StringComparison.Ordinal) || text[^1] != '\n')
        {
            throw new InvalidDataException("Missing or torn storage journal.");
        }
        var previous = new string('0', 64);
        var entries = new Dictionary<PrivateGenerationIdentity, PrivateGenerationJournalEntry>();
        var lines = text[Genesis.Length..].Split('\n');
        foreach (var line in lines[..^1])
        {
            var fields = line.Split('\t');
            if (fields.Length != 2 || fields[0].Length > 8192 || fields[1] != Hash(previous, fields[0]))
            {
                throw new InvalidDataException("Storage journal chain mismatch.");
            }
            var entry = JsonSerializer.Deserialize<PrivateGenerationJournalEntry>(
                Convert.FromBase64String(fields[0]), PrivateGenerationWire.Json)
                ?? throw new InvalidDataException("Empty storage journal entry.");
            if (entry.Identity.Domain.Id != layout.Enrollment.Domain.Id
                || entry.Identity.Domain.Layout != layout.Enrollment.Domain.Layout
                || entry.Identity.Domain.Epoch < 1 || entry.Identity.Domain.Epoch > layout.Enrollment.Domain.Epoch
                || entry.Revision < 1
                || entry.State is not ("Open" or "Sealed" or "Closing" or "Closed"))
            {
                throw new InvalidDataException("Invalid storage journal transition.");
            }
            var prior = entries.GetValueOrDefault(entry.Identity);
            var allowed = entry.State switch
            {
                "Open" => prior is null || prior.State == "Open",
                "Sealed" => prior?.State is "Open" or "Sealed",
                "Closing" => prior is null || prior.State is "Open" or "Sealed" or "Closing",
                "Closed" => prior?.State == "Closing"
                    || (prior?.State == "Closed" && prior.Revision == entry.Revision),
                _ => false,
            };
            if (!allowed)
            {
                throw new InvalidDataException("Nonmonotonic storage journal.");
            }
            entries[entry.Identity] = entry;
            previous = fields[1];
        }
        return (previous, entries);
    }

    internal PrivateGenerationJournalEntry? Find(PrivateGenerationIdentity identity) =>
        Read().Entries.GetValueOrDefault(identity);

    internal void Persist(PrivateGenerationIdentity identity, string state, long revision)
    {
        var snapshot = Read();
        var previous = snapshot.Entries.GetValueOrDefault(identity);
        if (previous?.State == "Closed" && (state != "Closed" || previous.Revision != revision))
        {
            throw new InvalidDataException("Storage closure is irreversible.");
        }
        if (previous?.State == state && previous.Revision == revision)
        {
            Synchronize();
            return;
        }
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new PrivateGenerationJournalEntry(identity, state, revision), PrivateGenerationWire.Json));
        var line = Encoding.UTF8.GetBytes(payload + "\t" + Hash(snapshot.Hash, payload) + "\n");
        using var file = new FileStream(layout.OpenControl("domain.journal"), FileAccess.ReadWrite);
        file.Seek(0, SeekOrigin.End);
        file.Write(line);
        file.Flush(flushToDisk: true);
    }

    internal void Synchronize()
    {
        _ = Read();
        using var file = layout.OpenControl("domain.journal");
        PrivateGenerationLinux.Sync(file);
    }

    private static string Hash(string previous, string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previous + "\n" + payload)));
}
