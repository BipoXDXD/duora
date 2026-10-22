using Duora.Core.Media;
using Npgsql;

namespace Duora.Infrastructure.Media;

internal sealed record ReviewPromotionCleanupClaim(Guid Generation, Guid Token, int Attempts, bool WriterWasSettled);

/// <summary>Unregistered seam: abandoned private promotions only, never existing assets.</summary>
internal sealed class ReviewPromotionCleanupDispatcher
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan UncertainWriterPoll = TimeSpan.FromSeconds(60);
    private readonly NpgsqlDataSource _source;

    internal ReviewPromotionCleanupDispatcher(NpgsqlDataSource source, IObjectStore store, TimeProvider time, bool enabled)
    {
        _source = source;
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        Enabled = enabled;
    }

    internal bool Enabled { get; }

    internal Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        _ = _source;
        cancellationToken.ThrowIfCancellationRequested();
        return Enabled ? throw new NotImplementedException("Promotion cleanup is not implemented.") : Task.FromResult(0);
    }

    internal Task<ReviewPromotionCleanupClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        _ = _source;
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("Promotion cleanup claims are not implemented.");
    }

    internal Task<bool> CompleteSweepAsync(ReviewPromotionCleanupClaim claim, CancellationToken cancellationToken)
    {
        _ = _source;
        ArgumentNullException.ThrowIfNull(claim);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("Promotion cleanup acknowledgement is not implemented.");
    }

    internal Task<bool> RetryAsync(ReviewPromotionCleanupClaim claim, CancellationToken cancellationToken)
    {
        _ = _source;
        ArgumentNullException.ThrowIfNull(claim);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("Promotion cleanup retries are not implemented.");
    }
}
