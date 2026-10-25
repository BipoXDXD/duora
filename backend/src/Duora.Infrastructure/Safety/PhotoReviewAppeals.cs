using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

internal abstract record PhotoReviewAppealRequestResult
{
    internal sealed record Accepted(Guid AppealId, Guid PhotoId, long PhotoVersion, DateTimeOffset RequestedAt,
        DateTimeOffset ResponseTargetAt, DateTimeOffset MaximumRetentionUntil) : PhotoReviewAppealRequestResult;
    internal sealed record Forbidden : PhotoReviewAppealRequestResult;
    internal sealed record NotFound : PhotoReviewAppealRequestResult;
    internal sealed record PreconditionFailed : PhotoReviewAppealRequestResult;
    internal sealed record KeyReused : PhotoReviewAppealRequestResult;
    internal sealed record StateConflict : PhotoReviewAppealRequestResult;
    internal sealed record Unavailable : PhotoReviewAppealRequestResult;
}

/// <summary>Regression scaffold only; no request, schema, registration or real appeal processing yet.</summary>
internal sealed class PhotoReviewAppeals
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;

    internal PhotoReviewAppeals(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        _contexts = contexts;
        _ = store;
        _ = time;
    }

    internal Task<PhotoReviewAppealRequestResult> RequestAsync(PhotoReviewActor owner, Guid photoId,
        long expectedPhotoVersion, IdempotencyKey key, CancellationToken cancellationToken)
    {
        _ = _contexts;
        return Task.FromResult<PhotoReviewAppealRequestResult>(new PhotoReviewAppealRequestResult.StateConflict());
    }
}
