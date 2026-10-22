using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

internal abstract record PhotoReviewEvidenceResult
{
    internal sealed record Opened(PhotoReviewEvidenceLease Lease) : PhotoReviewEvidenceResult;
    internal sealed record Forbidden : PhotoReviewEvidenceResult;
    internal sealed record NotFound : PhotoReviewEvidenceResult;
    internal sealed record Unavailable : PhotoReviewEvidenceResult;
}

internal sealed class PhotoReviewEvidenceRevokedException() : IOException("Review evidence access is no longer available.");

/// <summary>Fail-closed test seam; exclusively async, without a raw stream or public buffer.</summary>
internal sealed class PhotoReviewEvidenceLease : IAsyncDisposable
{
    private readonly Task<int> _denied = Task.FromException<int>(new PhotoReviewEvidenceRevokedException());

    internal PhotoReviewEvidenceLease(byte[] buffer, Func<CancellationToken, Task<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(authorize);
    }

    internal Task<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken) => _denied;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Unregistered M3c seam; no evidence bytes are delivered before its own behavioral RED.</summary>
internal sealed class PhotoReviewEvidence
{
    private readonly Task<PhotoReviewEvidenceResult> _denied = Task.FromResult<PhotoReviewEvidenceResult>(new PhotoReviewEvidenceResult.Forbidden());

    internal PhotoReviewEvidence(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
    }

    internal Task<PhotoReviewEvidenceResult> OpenAsync(
        PhotoReviewActor actor, Guid caseId, PhotoVariant variant, CancellationToken cancellationToken) => _denied;
}
