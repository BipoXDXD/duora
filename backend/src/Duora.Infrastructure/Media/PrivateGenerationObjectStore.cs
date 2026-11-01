using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

/// <summary>No private write can reach the legacy adapter through this process's generic PUT.</summary>
internal sealed class PrivateGenerationObjectStore(
    IObjectStore legacy, PrivateGenerationClient authority) : IObjectStore, IPrivateGenerationStore
{
    public PrivateGenerationDomain Domain => authority.Domain;

    public Task PutAsync(ObjectKey key, Stream content, long length, string contentType,
        CancellationToken cancellationToken) =>
        PrivateGenerationManifest.TryPrivateKey(key, out _, out _, out _)
            ? throw new InvalidOperationException("Private writes require a generation admission.")
            : legacy.PutAsync(key, content, length, contentType, cancellationToken);

    public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) =>
        PrivateGenerationManifest.TryPrivateKey(key, out _, out _, out _)
            ? authority.OpenReadAsync(key, cancellationToken) : legacy.OpenReadAsync(key, cancellationToken);

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
        PrivateGenerationManifest.TryPrivateKey(key, out _, out _, out _)
            ? authority.DeleteAsync(key, cancellationToken) : legacy.DeleteAsync(key, cancellationToken);

    public Task RegisterAsync(PrivateGenerationManifest manifest, CancellationToken cancellationToken) =>
        authority.RegisterAsync(manifest, cancellationToken);

    public Task PutVariantAsync(PrivateGenerationManifest manifest, PhotoVariant kind, Stream content,
        CancellationToken cancellationToken) => authority.PutVariantAsync(manifest, kind, content, cancellationToken);

    public Task SealAsync(PrivateGenerationManifest manifest, CancellationToken cancellationToken) =>
        authority.SealAsync(manifest, cancellationToken);

    public Task<PrivateGenerationCloseReceipt> CloseAsync(PrivateGenerationIdentity identity,
        CancellationToken cancellationToken) => authority.CloseAsync(identity, cancellationToken);

    public Task<Guid> SweepAsync(PrivateGenerationCloseReceipt receipt, Guid cleanupToken,
        CancellationToken cancellationToken) => authority.SweepAsync(receipt, cleanupToken, cancellationToken);
}
