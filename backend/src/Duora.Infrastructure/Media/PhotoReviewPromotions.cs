using Duora.Core.Media;
using Duora.Infrastructure.Persistence;

namespace Duora.Infrastructure.Media;

internal sealed record ReviewPromotionRequest(
    Guid CaseId, long CaseVersion, long UploadVersion, Guid CandidateGeneration, string PolicyVersion);

internal abstract record ReviewPromotionResult
{
    internal sealed record Prepared(Guid Generation) : ReviewPromotionResult;

    internal sealed record Unavailable : ReviewPromotionResult;
}

/// <summary>Unregistered test-first boundary; preparation never approves or publishes a photo.</summary>
internal sealed class PhotoReviewPromotions(DuoraDbContext db, IObjectStore store, TimeProvider time)
{
    internal Task<ReviewPromotionResult> PrepareAsync(ReviewPromotionRequest request, CancellationToken cancellationToken)
    {
        _ = (db, store, time, request, cancellationToken);
        throw new NotImplementedException();
    }
}
