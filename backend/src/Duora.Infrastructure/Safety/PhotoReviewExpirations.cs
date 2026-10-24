using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

/// <summary>Regression scaffold; not registered or exposed until the expiration contract is implemented.</summary>
internal sealed class PhotoReviewExpirations(IDbContextFactory<DuoraDbContext> contexts, TimeProvider time)
{
    internal Task<int> ExpireOnceAsync(CancellationToken cancellationToken)
    {
        _ = contexts;
        _ = time;
        return Task.FromResult(0);
    }
}
