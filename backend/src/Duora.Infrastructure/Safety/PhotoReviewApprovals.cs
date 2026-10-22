using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

internal enum PhotoReviewApprovalReason
{
    MeetsPolicy,
}

internal abstract record PhotoReviewApprovalResult
{
    internal sealed record Approved(Guid CaseId, long Version, long PhotoVersion, Guid AssetId) : PhotoReviewApprovalResult;
    internal sealed record Forbidden : PhotoReviewApprovalResult;
    internal sealed record NotFound : PhotoReviewApprovalResult;
    internal sealed record PreconditionFailed : PhotoReviewApprovalResult;
    internal sealed record KeyReused : PhotoReviewApprovalResult;
    internal sealed record StateConflict : PhotoReviewApprovalResult;
    internal sealed record Unavailable : PhotoReviewApprovalResult;
}

/// <summary>Unregistered initial approval seam. A private prepared copy is never an authorization.</summary>
internal sealed class PhotoReviewApprovals
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;

    internal PhotoReviewApprovals(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        _contexts = contexts;
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
    }

    internal Task<PhotoReviewApprovalResult> ApproveAsync(PhotoReviewActor actor, Guid caseId, long expectedVersion,
        PhotoReviewApprovalReason reason, IdempotencyKey key, CancellationToken cancellationToken)
    {
        _ = _contexts;
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("Authorized human approval is not implemented.");
    }
}
