using System.Net.Sockets;
using System.Runtime.Versioning;
using Barrier = Duora.ClosureProbe.Barrier;

namespace Duora.ClosureBoundary;

internal enum WitnessStatus { Open, Closed, Busy, Denied, RecoveryRequired }
internal sealed record WitnessReply(WitnessStatus Status, long Revision = 0);

/// <summary>Independent local test process/volume, not an operational external ledger.</summary>
[SupportedOSPlatform("linux")]
internal sealed class RevocationController(ProtectedLayout layout, Barrier barrier)
{
    internal WitnessReply Handle(PeerIdentity peer, ProbeRequest request)
    {
        var generation = layout.Match(request.Grant);
        if (peer.Uid != 17010 || peer.Gid != 17010 || request.Version != 2 || generation is null
            || generation.Committed || request.Length != 0 || request.Operation is not ("read" or "close"))
        {
            return new WitnessReply(WitnessStatus.Denied);
        }

        layout.Validate();
        using var gate = layout.OpenControl("domain.lock");
        if (!LinuxBoundary.TryLock(gate))
        {
            return new WitnessReply(WitnessStatus.Busy);
        }

        var journal = new ControlJournal(layout, witness: true, barrier);
        var snapshot = journal.Read();
        if (snapshot.Generations.TryGetValue(generation.Generation, out var state))
        {
            journal.Synchronize();
            return new WitnessReply(WitnessStatus.Closed, state.WitnessRevision);
        }

        if (request.Operation == "read")
        {
            return new WitnessReply(WitnessStatus.Open);
        }

        var revision = journal.AppendAndSync(generation, JournalState.Closed, 0);
        return new WitnessReply(WitnessStatus.Closed, revision);
    }
}

[SupportedOSPlatform("linux")]
internal static class WitnessClient
{
    internal static async Task<WitnessReply> CallAsync(string operation, WriteGrant grant, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint("/state/rpc/controller/service.sock"), cancellationToken);
        // Bind the other end to the expected controller, not merely a replaceable socket name.
        var peer = LinuxBoundary.Peer(socket);
        if (peer.Uid != 17030 || peer.Gid != 17030)
        {
            throw new InvalidDataException("Untrusted witness peer.");
        }

        using var stream = new NetworkStream(socket, ownsSocket: false);
        await Frame.WriteObjectAsync(stream, new ProbeRequest(2, operation, grant, "thumb", 0), cancellationToken);
        socket.Shutdown(SocketShutdown.Send);
        var reply = await Frame.ReadObjectAsync<WitnessReply>(stream, cancellationToken);
        if ((reply.Status == WitnessStatus.Closed && reply.Revision > 0)
            || (reply.Status == WitnessStatus.Open && reply.Revision == 0))
        {
            return reply;
        }

        throw new IOException("Witness did not confirm a valid state.");
    }
}
