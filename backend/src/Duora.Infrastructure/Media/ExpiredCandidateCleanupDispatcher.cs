using Duora.Core.Media;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.Infrastructure.Media;

internal sealed record ExpiredCandidateCleanupClaim(Guid Generation, Guid Token, int Attempts);

/// <summary>Regression scaffold for audited system expirations; not registered or collecting yet.</summary>
public sealed class ExpiredCandidateCleanupDispatcher
{
    internal ExpiredCandidateCleanupDispatcher(NpgsqlDataSource source, IObjectStore store, TimeProvider time,
        bool enabled, ILogger<ExpiredCandidateCleanupDispatcher> logger)
    {
        _ = source;
        _ = store;
        _ = time;
        _ = logger;
        Enabled = enabled;
    }

    public bool Enabled { get; }

    public Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        _ = Enabled;
        return Task.FromResult(0);
    }

    internal Task<ExpiredCandidateCleanupClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        _ = Enabled;
        return Task.FromResult<ExpiredCandidateCleanupClaim?>(null);
    }

    internal Task<bool> CompleteSweepAsync(ExpiredCandidateCleanupClaim claim, CancellationToken cancellationToken)
    {
        _ = Enabled;
        return Task.FromResult(false);
    }
}
