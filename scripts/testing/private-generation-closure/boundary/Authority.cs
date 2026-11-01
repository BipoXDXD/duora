using System.Net.Sockets;
using System.Runtime.Versioning;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Barrier = Duora.ClosureProbe.Barrier;

namespace Duora.ClosureBoundary;

[SupportedOSPlatform("linux")]
internal sealed class Authority(ProtectedLayout layout, Barrier barrier)
{
    internal async Task<ProbeReply> HandleAsync(PeerIdentity peer, ProbeRequest request, Stream body, CancellationToken cancellationToken)
    {
        var registration = layout.Match(request.Grant);
        var writer = peer.Uid == 17020 && peer.Gid == 17020 && request.Operation == "put";
        var control = peer.Uid == 17010 && peer.Gid == 17010 && request.Operation is "close" or "sweep";
        if ((!writer && !control) || request.Version != 2 || registration is null || registration.Committed
            || request.Length is < 0 or > 98304 || (!writer && request.Length != 0)
            || request.Variant is not ("thumb" or "card" or "full"))
        {
            return new ProbeReply(ReplyCode.Denied);
        }

        layout.Validate();
        using var gate = layout.OpenControl("domain.lock");
        if (!LinuxBoundary.TryLock(gate))
        {
            return new ProbeReply(ReplyCode.Busy);
        }

        // Revalidate the path bindings after acquisition; never recreate a missing lock/journal.
        layout.Validate();
        var journal = new ControlJournal(layout, witness: false, barrier);
        var snapshot = journal.Read();
        if (!snapshot.Generations.TryGetValue(registration.Generation, out var state))
        {
            return new ProbeReply(ReplyCode.RecoveryRequired);
        }

        if (writer)
        {
            if (state.State is JournalState.Closing or JournalState.Closed)
            {
                return new ProbeReply(ReplyCode.Denied);
            }

            if (state.State != JournalState.Open)
            {
                return new ProbeReply(ReplyCode.AdmissionBlocked);
            }

            WitnessReply witness;
            try
            {
                witness = await WitnessClient.CallAsync("read", request.Grant!, cancellationToken);
            }
            catch (Exception error) when (error is IOException or SocketException or System.Text.Json.JsonException or OperationCanceledException)
            {
                Console.Error.WriteLine($"WITNESS_UNAVAILABLE {error.GetType().Name}");
                return new ProbeReply(ReplyCode.AdmissionBlocked);
            }

            if (witness.Status != WitnessStatus.Open)
            {
                return new ProbeReply(ReplyCode.Denied);
            }

            var variant = request.Variant switch { "thumb" => PhotoVariant.Thumb, "card" => PhotoVariant.Card, _ => PhotoVariant.Full };
            barrier.Wait("before-put", (int)variant);
            var store = new FileSystemObjectStore(Path.Combine(layout.Root, "data"), createMissingDirectories: false);
            await using (var paused = new PausedBody(body, request.Length, (int)variant, barrier))
            {
                await store.PutAsync(Key(registration, variant), paused, request.Length, "application/octet-stream", cancellationToken);
            }

            barrier.Wait("after-put", (int)variant);
            return new ProbeReply(ReplyCode.Written);
        }

        if (request.Operation == "close")
        {
            if (state.State == JournalState.Reserved)
            {
                return new ProbeReply(ReplyCode.AdmissionBlocked);
            }

            if (state.State == JournalState.Open)
            {
                journal.AppendAndSync(registration, JournalState.Closing, 0);
            }
            else
            {
                journal.Synchronize();
            }

            var witness = await WitnessClient.CallAsync("close", request.Grant!, cancellationToken);
            if (witness.Status != WitnessStatus.Closed)
            {
                throw new InvalidDataException("Witness closure missing.");
            }

            barrier.Wait("witness-confirmed");
            if (state.State != JournalState.Closed)
            {
                journal.AppendAndSync(registration, JournalState.Closed, witness.Revision);
            }
            else if (state.WitnessRevision != witness.Revision)
            {
                throw new InvalidDataException("Witness revision mismatch.");
            }

            return new ProbeReply(ReplyCode.ClosedExperimental, witness.Revision);
        }

        if (state.State != JournalState.Closed)
        {
            return new ProbeReply(ReplyCode.Denied);
        }

        journal.Synchronize();

        var proof = await WitnessClient.CallAsync("read", request.Grant!, cancellationToken);
        if (proof.Status != WitnessStatus.Closed || proof.Revision != state.WitnessRevision)
        {
            throw new InvalidDataException("Closure proof missing before sweep.");
        }

        var dataStore = new FileSystemObjectStore(Path.Combine(layout.Root, "data"), createMissingDirectories: false);
        var area = registration.Area == "candidate" ? "review-candidates" : "derivatives";
        var directory = Path.Combine(layout.Root, "data", area);
        foreach (var variant in PhotoVariants.All)
        {
            var key = Key(registration, variant);
            await dataStore.DeleteAsync(key, cancellationToken);
            var name = Path.GetFileName(key.Value);
            foreach (var path in Directory.EnumerateFiles(directory, name + ".*.partial"))
            {
                var suffix = Path.GetFileName(path)[(name.Length + 1)..];
                if (suffix.Length == 40 && suffix.EndsWith(".partial", StringComparison.Ordinal)
                    && suffix[..32].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    File.Delete(path);
                }
            }

            barrier.Wait("after-delete", (int)variant);
        }

        barrier.Wait("removal-sync");
        // The area is intentionally writable by the trusted authority; still reject symlinks here.
        using var data = LinuxBoundary.OpenDirectory(Path.Combine(layout.Root, "data"));
        using var areaHandle = LinuxBoundary.OpenChild(data, area, directory: true);
        LinuxBoundary.Verify(areaHandle, layout.Enrollment.Data[area], 17010, 17010, 448, directory: true);
        LinuxBoundary.Sync(areaHandle);
        return new ProbeReply(ReplyCode.SweptExperimental, state.WitnessRevision);
    }

    private static ObjectKey Key(GenerationRegistration generation, PhotoVariant variant) => generation.Area == "candidate"
        ? ObjectKey.ForReviewCandidate(generation.Generation, variant) : ObjectKey.ForPromotedDerivative(generation.Generation, variant);
}
