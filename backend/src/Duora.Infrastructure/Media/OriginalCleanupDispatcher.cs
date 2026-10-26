using Duora.Core.Media;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.Infrastructure.Media;

/// <summary>The random ownership token also prevents ABA if an upload is enqueued again after completion.</summary>
internal sealed record OriginalCleanupClaim(Guid UploadId, ObjectKey Key, Guid Token, int Attempts);

/// <summary>One original per lease. All storage I/O occurs after the claim connection has been released.</summary>
public sealed partial class OriginalCleanupDispatcher
{
    internal static readonly TimeSpan DeleteBudget = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private readonly NpgsqlDataSource _source;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<OriginalCleanupDispatcher> _logger;

    internal OriginalCleanupDispatcher(
        NpgsqlDataSource source,
        IObjectStore store,
        TimeProvider time,
        bool enabled,
        ILogger<OriginalCleanupDispatcher> logger)
    {
        _source = source;
        _store = store;
        _time = time;
        Enabled = enabled;
        _logger = logger;
    }

    public bool Enabled { get; }

    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || await ClaimAsync(cancellationToken) is not { } claim)
        {
            return 0;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DeleteBudget);
        try
        {
            await _store.DeleteAsync(claim.Key, budget.Token);
            budget.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OutOfMemoryException)
        {
            LogRetry(_logger, claim.UploadId, claim.Attempts, exception.GetType().Name);
            await RetryAsync(claim, cancellationToken);
            return 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!await CompleteAsync(claim, cancellationToken))
        {
            LogLeaseLost(_logger, claim.UploadId);
        }

        return 1;
    }

    internal async Task<OriginalCleanupClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var token = Guid.NewGuid();
        await using var command = Command(connection, """
            with picked as materialized (
              select c.upload_id from media.original_cleanup c
              join media.uploads u on u.id = c.upload_id and u.quarantine_key = c.object_key
              where c.available_at <= $1 and (c.lease_until is null or c.lease_until <= $1)
                and u.state in ('Approved', 'Rejected', 'Deleted', 'AwaitingReview')
              order by c.available_at, c.upload_id limit 1 for update of c skip locked)
            update media.original_cleanup c
            set fencing_token = $2, lease_until = $3,
                attempts = case when attempts < 1000000 then attempts + 1 else attempts end
            from picked where c.upload_id = picked.upload_id
            returning c.upload_id, c.object_key, c.attempts
            """, now, token, now + LeaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OriginalCleanupClaim(reader.GetGuid(0), ObjectKey.Parse(reader.GetString(1)), token, reader.GetInt32(2))
            : null;
    }

    internal async Task<bool> CompleteAsync(OriginalCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockObligationAsync(connection, transaction, claim, cancellationToken))
        {
            return false;
        }

        await using var command = Command(connection, """
            delete from media.original_cleanup where upload_id = $1 and object_key = $2
              and fencing_token = $3 and lease_until > $4
            """, claim.UploadId, claim.Key.Value, claim.Token, _time.GetUtcNow());
        command.Transaction = transaction;
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    internal async Task<bool> RetryAsync(OriginalCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockObligationAsync(connection, transaction, claim, cancellationToken))
        {
            return false;
        }

        var now = _time.GetUtcNow();
        var delay = TimeSpan.FromSeconds(Math.Min(900, 15 * (1 << Math.Min(claim.Attempts - 1, 6))));
        await using var command = Command(connection, """
            update media.original_cleanup set available_at = $4, lease_until = null, fencing_token = null
            where upload_id = $1 and object_key = $2 and fencing_token = $3 and lease_until > $5
            """, claim.UploadId, claim.Key.Value, claim.Token, now + delay, now);
        command.Transaction = transaction;
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private static async Task<bool> LockObligationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        OriginalCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var command = Command(connection,
            "select upload_id from media.original_cleanup where upload_id=$1 and object_key=$2 for update",
            claim.UploadId, claim.Key.Value);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Original cleanup for upload {UploadId} failed on attempt {Attempt} ({Cause}); durable retry retained")]
    private static partial void LogRetry(ILogger logger, Guid uploadId, int attempt, string cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Original cleanup for upload {UploadId} lost its lease; persisted ownership protects recovery")]
    private static partial void LogLeaseLost(ILogger logger, Guid uploadId);
}
