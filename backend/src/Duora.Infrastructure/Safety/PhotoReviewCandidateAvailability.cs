using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

/// <summary>Regression scaffold only; no reconciliation, registration or processing yet.</summary>
internal sealed class PhotoReviewCandidateAvailability
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;

    internal PhotoReviewCandidateAvailability(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        _contexts = contexts;
        _ = store;
        _ = time;
    }

    internal Task<int> ReconcileAsync(Guid caseId, CancellationToken cancellationToken)
    {
        _ = _contexts;
        return Task.FromResult(0);
    }
}
