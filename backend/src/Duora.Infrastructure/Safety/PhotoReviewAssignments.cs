using Duora.Core.Media;
using Duora.Infrastructure.Persistence;

namespace Duora.Infrastructure.Safety;

internal abstract record PhotoReviewAssignmentResult
{
    internal sealed record Assigned(Guid CaseId, long Version) : PhotoReviewAssignmentResult;
    internal sealed record Forbidden : PhotoReviewAssignmentResult;
    internal sealed record NotFound : PhotoReviewAssignmentResult;
    internal sealed record PreconditionFailed : PhotoReviewAssignmentResult;
    internal sealed record KeyReused : PhotoReviewAssignmentResult;
    internal sealed record ReviewerUnavailable : PhotoReviewAssignmentResult;
    internal sealed record StateConflict : PhotoReviewAssignmentResult;
}

internal readonly record struct PhotoReviewCursor(DateTimeOffset AwaitingReviewAt, Guid CaseId);
internal sealed record PhotoReviewSummary(Guid CaseId, string State, long Version, DateTimeOffset AwaitingReviewAt, DateTimeOffset ReviewDeadline);
internal sealed record PhotoReviewPage(IReadOnlyList<PhotoReviewSummary> Cases, PhotoReviewCursor? NextCursor);
internal sealed record PhotoReviewDetails(PhotoReviewSummary Case, string PolicyVersion);

internal abstract record PhotoReviewListResult
{
    internal sealed record Listed(PhotoReviewPage Page) : PhotoReviewListResult;
    internal sealed record Forbidden : PhotoReviewListResult;
}

internal abstract record PhotoReviewDetailsResult
{
    internal sealed record Found(PhotoReviewDetails Details) : PhotoReviewDetailsResult;
    internal sealed record Forbidden : PhotoReviewDetailsResult;
    internal sealed record NotFound : PhotoReviewDetailsResult;
}

/// <summary>Unregistered fail-closed seam for M3b tests; no assignment or metadata access is delivered yet.</summary>
internal sealed class PhotoReviewAssignments
{
    private readonly Task<PhotoReviewAssignmentResult> _denied = Task.FromResult<PhotoReviewAssignmentResult>(new PhotoReviewAssignmentResult.Forbidden());
    private readonly Task<PhotoReviewListResult> _noList = Task.FromResult<PhotoReviewListResult>(new PhotoReviewListResult.Forbidden());
    private readonly Task<PhotoReviewDetailsResult> _noDetails = Task.FromResult<PhotoReviewDetailsResult>(new PhotoReviewDetailsResult.Forbidden());

    internal PhotoReviewAssignments(DuoraDbContext db, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
    }

    internal Task<PhotoReviewAssignmentResult> AssignAsync(
        PhotoReviewActor actor, Guid caseId, Guid reviewerId, long expectedVersion, IdempotencyKey key,
        CancellationToken cancellationToken) => _denied;

    internal Task<PhotoReviewListResult> ListAssignedAsync(
        PhotoReviewActor actor, PhotoReviewCursor? cursor, int limit, CancellationToken cancellationToken) => _noList;

    internal Task<PhotoReviewDetailsResult> FindAssignedAsync(
        PhotoReviewActor actor, Guid caseId, CancellationToken cancellationToken) => _noDetails;
}
