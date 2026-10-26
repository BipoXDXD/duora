using Duora.Core.Media;
using Npgsql;

namespace Duora.Infrastructure.Media;

/// <summary>Writer settlement is captured at claim time, before this sweep starts any DELETE.</summary>
internal sealed record ReviewPromotionCleanupClaim(Guid Generation, Guid Token, int Attempts, bool WriterWasSettled);

/// <summary>Unregistered collector for abandoned generation-owned promotions; committed assets are excluded.</summary>
internal sealed class ReviewPromotionCleanupDispatcher
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DeleteBudget = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan UncertainWriterPoll = TimeSpan.FromSeconds(60);
    private readonly NpgsqlDataSource _source;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;

    internal ReviewPromotionCleanupDispatcher(
        NpgsqlDataSource source, IObjectStore store, TimeProvider time, bool enabled)
    {
        _source = source;
        _store = store;
        _time = time;
        Enabled = enabled;
    }

    internal bool Enabled { get; }

    internal async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || await ClaimAsync(cancellationToken) is not { } claim)
        {
            return 0;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DeleteBudget);
        try
        {
            foreach (var kind in PhotoVariants.All)
            {
                await _store.DeleteAsync(ObjectKey.ForPromotedDerivative(claim.Generation, kind), budget.Token);
                budget.Token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OutOfMemoryException)
        {
            await RetryAsync(claim, cancellationToken);
            return 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // A lost lease cannot acknowledge another collector's sweep. Its persisted claim recovers.
        await CompleteSweepAsync(claim, cancellationToken);

        return 1;
    }

    internal async Task<ReviewPromotionCleanupClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var token = Guid.NewGuid();
        await using var command = Command(connection, """
            with picked as materialized (
              select generation from media.review_promotions
              where committed_at is null and deleted_at is null and cleanup_available_at <= $1
                and (cleanup_requested_at is not null or prepare_deadline <= $1)
                and (cleanup_lease_until is null or cleanup_lease_until <= $1)
              order by cleanup_available_at,generation limit 1 for update skip locked)
            update media.review_promotions c
            set cleanup_requested_at=coalesce(cleanup_requested_at,$1),cleanup_token=$2,cleanup_lease_until=$3,
              cleanup_attempts=case when cleanup_attempts < 1000000 then cleanup_attempts+1 else cleanup_attempts end
            from picked where c.generation=picked.generation
            returning c.generation,c.cleanup_attempts,c.writer_settled_at is not null
            """, now, token, now + LeaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ReviewPromotionCleanupClaim(reader.GetGuid(0), token, reader.GetInt32(1), reader.GetBoolean(2))
            : null;
    }

    internal async Task<bool> CompleteSweepAsync(ReviewPromotionCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockPromotionAsync(connection, transaction, claim.Generation, cancellationToken))
        {
            return false;
        }

        var now = _time.GetUtcNow();
        await using var command = Command(connection, """
            update media.review_promotions set last_cleanup_at=$4,
              deleted_at=case when $3 then $4 else null end,
              cleanup_available_at=$5,cleanup_token=null,cleanup_lease_until=null
            where generation=$1 and cleanup_token=$2 and cleanup_lease_until>$4
              and committed_at is null and deleted_at is null and cleanup_requested_at is not null
              and (not $3 or writer_settled_at is not null)
            """, claim.Generation, claim.Token, claim.WriterWasSettled, now, now + UncertainWriterPoll);
        // An uncertain writer never has a terminal ACK, even if it settled while these DELETEs were running.
        command.Transaction = transaction;
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    internal async Task<bool> RetryAsync(ReviewPromotionCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockPromotionAsync(connection, transaction, claim.Generation, cancellationToken))
        {
            return false;
        }

        var now = _time.GetUtcNow();
        var delay = TimeSpan.FromSeconds(Math.Min(900, 15 * (1 << Math.Min(claim.Attempts - 1, 6))));
        await using var command = Command(connection, """
            update media.review_promotions set cleanup_available_at=$4,cleanup_token=null,cleanup_lease_until=null
            where generation=$1 and cleanup_token=$2 and cleanup_lease_until>$3
              and committed_at is null and deleted_at is null and cleanup_requested_at is not null
            """, claim.Generation, claim.Token, now, now + delay);
        command.Transaction = transaction;
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private static async Task<bool> LockPromotionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid generation, CancellationToken cancellationToken)
    {
        await using var command = Command(connection,
            "select generation from media.review_promotions where generation=$1 for update", generation);
        command.Transaction = transaction;
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 3 };
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }

        return command;
    }
}
