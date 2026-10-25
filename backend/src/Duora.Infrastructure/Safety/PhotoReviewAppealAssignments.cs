using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

internal abstract record PhotoReviewAppealAssignmentResult
{
    internal sealed record Assigned(Guid AppealId, long Version) : PhotoReviewAppealAssignmentResult;
    internal sealed record Forbidden : PhotoReviewAppealAssignmentResult;
    internal sealed record NotFound : PhotoReviewAppealAssignmentResult;
    internal sealed record PreconditionFailed : PhotoReviewAppealAssignmentResult;
    internal sealed record KeyReused : PhotoReviewAppealAssignmentResult;
    internal sealed record ReviewerUnavailable : PhotoReviewAppealAssignmentResult;
    internal sealed record StateConflict : PhotoReviewAppealAssignmentResult;
}

/// <summary>Regression scaffold only; no assignment, schema, registration or real reviewer is enabled.</summary>
internal sealed class PhotoReviewAppealAssignments
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;

    internal PhotoReviewAppealAssignments(IDbContextFactory<DuoraDbContext> contexts, TimeProvider time)
    {
        _contexts = contexts;
        _ = time;
    }

    internal Task<PhotoReviewAppealAssignmentResult> AssignAsync(PhotoReviewActor actor, Guid appealId,
        Guid reviewerId, long expectedVersion, IdempotencyKey key, CancellationToken cancellationToken)
    {
        _ = _contexts;
        return Task.FromResult<PhotoReviewAppealAssignmentResult>(new PhotoReviewAppealAssignmentResult.StateConflict());
    }
}
