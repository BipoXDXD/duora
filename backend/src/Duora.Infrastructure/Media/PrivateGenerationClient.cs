using System.Net.Sockets;
using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

internal sealed class PrivateGenerationClient(PrivateGenerationOptions options) : IPrivateGenerationStore
{
    public PrivateGenerationDomain Domain => options.Domain;

    public async Task RegisterAsync(PrivateGenerationManifest manifest, CancellationToken cancellationToken)
    {
        manifest.Validate();
        await CallAsync(new(1, "register", manifest.Identity, manifest), null, cancellationToken);
    }

    public async Task PutVariantAsync(PrivateGenerationManifest manifest, PhotoVariant kind, Stream content,
        CancellationToken cancellationToken)
    {
        manifest.Validate();
        var variant = manifest.Variants.Single(v => v.Kind == kind);
        await CallAsync(new(1, "put", manifest.Identity, manifest, kind, variant.Length), content, cancellationToken);
    }

    public async Task SealAsync(PrivateGenerationManifest manifest, CancellationToken cancellationToken)
    {
        manifest.Validate();
        await CallAsync(new(1, "seal", manifest.Identity, manifest), null, cancellationToken);
    }

    public async Task<PrivateGenerationCloseReceipt> CloseAsync(PrivateGenerationIdentity identity,
        CancellationToken cancellationToken)
    {
        var (reply, _) = await CallAsync(new(1, "close", identity), null, cancellationToken);
        if (reply.Revision < 1)
        {
            throw new InvalidDataException("Missing storage closure revision.");
        }
        return new(identity, reply.Revision);
    }

    public async Task<Guid> SweepAsync(PrivateGenerationCloseReceipt receipt, Guid cleanupToken,
        CancellationToken cancellationToken)
    {
        var (reply, _) = await CallAsync(new(1, "sweep", receipt.Identity,
            ClosedRevision: receipt.Revision, CleanupToken: cleanupToken), null, cancellationToken);
        if (reply.ProofId == Guid.Empty)
        {
            throw new InvalidDataException("Missing storage sweep evidence.");
        }
        return reply.ProofId;
    }

    internal async Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var (reply, body) = await CallAsync(new(1, "read", ObjectKey: key.Value), null, cancellationToken);
        return reply.Status == "missing" ? null : new MemoryStream(body, writable: false);
    }

    internal async Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
        _ = await CallAsync(new(1, "delete", ObjectKey: key.Value), null, cancellationToken);

    private async Task<(PrivateGenerationReply Reply, byte[] Body)> CallAsync(
        PrivateGenerationRequest request, Stream? body, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Private storage peer authentication requires Linux.");
        }
        Domain.Validate();
        if (request.Identity is { } identity && (identity.Domain.Id != Domain.Id
            || identity.Domain.Layout != Domain.Layout || identity.Domain.Epoch < 1 || identity.Domain.Epoch > Domain.Epoch
            || (request.Operation is "register" or "put" or "seal" && identity.Domain != Domain)))
        {
            throw new InvalidDataException("Storage domain mismatch.");
        }
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), cancellationToken);
        var peer = PrivateGenerationLinux.Peer(socket);
        if (peer.Uid != options.AuthorityUid || peer.Gid != options.AuthorityUid || peer.Uid == 0)
        {
            throw new InvalidDataException("Untrusted storage authority peer.");
        }
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await PrivateGenerationWire.WriteAsync(stream, request, cancellationToken);
        if (body is not null)
        {
            var admission = await PrivateGenerationWire.ReadAsync<PrivateGenerationReply>(stream, cancellationToken);
            if (admission.Status != "admitted" || admission.Length != 0)
            {
                throw new IOException("Private storage did not admit the variant body.");
            }
            await using var exact = new ExactLengthStream(body, request.Length);
            await exact.CopyToAsync(stream, cancellationToken);
        }
        socket.Shutdown(SocketShutdown.Send);
        return await PrivateGenerationWire.ReadResponseAsync(stream, cancellationToken);
    }
}
