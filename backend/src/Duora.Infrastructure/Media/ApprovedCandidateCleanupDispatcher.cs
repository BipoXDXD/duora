using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.Infrastructure.Media;

internal sealed record ApprovedCandidateCleanupClaim(Guid Generation, Guid Token, int Attempts);

/// <summary>Regression scaffold: no confirmed candidate is collected until the reviewed implementation exists.</summary>
public sealed class ApprovedCandidateCleanupDispatcher
{
    internal ApprovedCandidateCleanupDispatcher(NpgsqlDataSource source, Duora.Core.Media.IObjectStore store,
        TimeProvider time, bool enabled, ILogger<ApprovedCandidateCleanupDispatcher> logger)
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

    internal Task<ApprovedCandidateCleanupClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        _ = Enabled;
        return Task.FromResult<ApprovedCandidateCleanupClaim?>(null);
    }

    internal Task<bool> CompleteSweepAsync(ApprovedCandidateCleanupClaim claim, CancellationToken cancellationToken)
    {
        _ = Enabled;
        return Task.FromResult(false);
    }
}
