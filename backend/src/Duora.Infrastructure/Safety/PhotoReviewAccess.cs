using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;

namespace Duora.Infrastructure.Safety;

internal enum PhotoReviewCapability
{
    Assign,
    Evidence,
    Decide,
    Appeal,
    Preserve,
}

/// <summary>Identifiers from the authenticated boundary, never a public authentication credential.</summary>
internal readonly record struct PhotoReviewActor(Guid UserId, Guid SessionId);

/// <summary>Unregistered fail-closed seam for the M3a regression tests; no permissions or access are delivered yet.</summary>
internal sealed class PhotoReviewAccess
{
    private readonly Task<bool> _denied = Task.FromResult(false);

    internal PhotoReviewAccess(DuoraDbContext db, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
    }

    internal Task<bool> SetCapabilityAsync(
        Guid userId, PhotoReviewCapability capability, bool enabled, OperatorName operatorName, CancellationToken cancellationToken) => _denied;

    internal Task<bool> AuthorizeAsync(
        PhotoReviewActor actor, PhotoReviewCapability capability, CancellationToken cancellationToken) => _denied;
}
