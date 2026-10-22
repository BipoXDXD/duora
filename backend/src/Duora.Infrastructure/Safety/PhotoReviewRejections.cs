using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

internal enum PhotoReviewRejectionReason
{
    DoesNotMeetPolicy,
}

internal abstract record PhotoReviewRejectionResult
{
    internal sealed record Rejected(Guid CaseId, long Version, long PhotoVersion) : PhotoReviewRejectionResult;
    internal sealed record Forbidden : PhotoReviewRejectionResult;
    internal sealed record NotFound : PhotoReviewRejectionResult;
    internal sealed record PreconditionFailed : PhotoReviewRejectionResult;
    internal sealed record KeyReused : PhotoReviewRejectionResult;
    internal sealed record StateConflict : PhotoReviewRejectionResult;
    internal sealed record Unavailable : PhotoReviewRejectionResult;
}

/// <summary>Unregistered fail-closed seam for independent M4a rejection specifications; no real decision.</summary>
internal sealed class PhotoReviewRejections
{
    private readonly Task<PhotoReviewRejectionResult> _denied =
        Task.FromResult<PhotoReviewRejectionResult>(new PhotoReviewRejectionResult.Forbidden());

    internal PhotoReviewRejections(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
    }

    internal Task<PhotoReviewRejectionResult> RejectAsync(PhotoReviewActor actor, Guid caseId, long expectedVersion,
        PhotoReviewRejectionReason reason, IdempotencyKey key, CancellationToken cancellationToken) => _denied;
}
