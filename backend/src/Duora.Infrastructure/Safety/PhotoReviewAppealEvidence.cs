using Duora.Core.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

/// <summary>Regression scaffold only; no appeal evidence is exposed or registered.</summary>
internal sealed class PhotoReviewAppealEvidence
{
    private readonly IDbContextFactory<DuoraDbContext> _contexts;

    internal PhotoReviewAppealEvidence(IDbContextFactory<DuoraDbContext> contexts, IObjectStore store, TimeProvider time)
    {
        _contexts = contexts;
        _ = store;
        _ = time;
    }

    internal Task<PhotoReviewEvidenceResult> OpenAsync(PhotoReviewActor actor, Guid appealId,
        PhotoVariant variant, CancellationToken cancellationToken)
    {
        _ = _contexts;
        return Task.FromResult<PhotoReviewEvidenceResult>(new PhotoReviewEvidenceResult.Forbidden());
    }
}
