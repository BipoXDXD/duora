using Duora.Core.Media;
using Duora.Infrastructure.Persistence;

namespace Duora.Infrastructure.Media;

internal abstract record ReviewCandidateStageResult
{
    internal sealed record Staged(Guid CaseId) : ReviewCandidateStageResult;

    internal sealed record LeaseLost : ReviewCandidateStageResult;
}

/// <summary>
/// Compile-time seam for the RED of ADR-0035/M2b-2. Not registered or invoked by the live pipeline.
/// Durable preparation, confirmation and recovery must pass the synthetic CI before this seam is implemented.
/// </summary>
internal sealed class PhotoReviewCandidates
{
    internal PhotoReviewCandidates(DuoraDbContext db, IObjectStore store, TimeProvider time, MediaProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
    }

    internal Task<ReviewCandidateStageResult> StageAsync(
        Guid uploadId,
        long expectedVersion,
        DateTimeOffset leaseExpiresAt,
        SanitizeOutcome.Sanitized sanitized,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ReviewCandidateStageResult>(new ReviewCandidateStageResult.LeaseLost());
    }
}
