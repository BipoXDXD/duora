using System.Runtime.Versioning;
using Duora.Core.Media;
using Duora.Infrastructure.Media;

namespace Duora.ClosureBoundary;

[SupportedOSPlatform("linux")]
internal sealed class Authority(ProtectedLayout layout)
{
    internal async Task<ProbeReply> HandleAsync(PeerIdentity peer, ProbeRequest request, Stream body, CancellationToken cancellationToken)
    {
        var registration = layout.Match(request.Grant);
        if (peer.Uid != 17020 || request.Version != 2 || registration is null || registration.Committed
            || request.Operation != "put" || request.Length is < 0 or > 98304
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

        var variant = request.Variant switch { "thumb" => PhotoVariant.Thumb, "card" => PhotoVariant.Card, _ => PhotoVariant.Full };
        var key = registration.Area == "candidate" ? ObjectKey.ForReviewCandidate(registration.Generation, variant)
            : ObjectKey.ForPromotedDerivative(registration.Generation, variant);
        var store = new FileSystemObjectStore(Path.Combine(layout.Root, "data"), createMissingDirectories: false);
        await store.PutAsync(key, body, request.Length, "application/octet-stream", cancellationToken);
        return new ProbeReply(ReplyCode.Written);
    }
}
