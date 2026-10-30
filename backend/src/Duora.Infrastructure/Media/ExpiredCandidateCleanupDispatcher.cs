using Duora.Core.Media;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.Infrastructure.Media;

internal sealed record ExpiredCandidateCleanupClaim(Guid Generation, Guid Token, int Attempts);

/// <summary>Consumes only the private cleanup obligation committed with its immutable system expiration fact.</summary>
public sealed partial class ExpiredCandidateCleanupDispatcher
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DeleteBudget = TimeSpan.FromSeconds(15);
    private readonly NpgsqlDataSource _source;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<ExpiredCandidateCleanupDispatcher> _logger;

    // An expired code, old file or foreign audit identity alone is never an enrollment authority.
    private const string ExpiredObligation = """
        c.committed_at is not null and c.writer_settled_at is not null
        and c.deleted_at is null and c.cleanup_requested_at is not null and c.cleanup_audit_id is not null
        and exists (
          select 1 from safety.photo_review_cases r
          join media.uploads u on u.id=r.upload_id
          join ops.audit_events e on e.id=c.cleanup_audit_id
          where r.id=c.case_id and r.upload_id=c.upload_id and r.candidate_generation=c.generation
            and r.policy_version='human-review-v1'
            and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_EXPIRED'
            and c.upload_version=u.version-2 and c.retention_until=r.review_deadline+interval '24 hours'
            and c.cleanup_requested_at>=r.review_deadline
            and e.actor_kind='System' and e.actor_id is null and e.action='safety.photo_review_expired'
            and e.target_type='photo_review_case' and e.target_id=r.id and e.target_version=r.version)
        """;

    internal ExpiredCandidateCleanupDispatcher(NpgsqlDataSource source, IObjectStore store, TimeProvider time,
        bool enabled, ILogger<ExpiredCandidateCleanupDispatcher> logger)
    {
        _source = source;
        _store = store;
        _time = time;
        _logger = logger;
        Enabled = enabled;
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
            foreach (var kind in PhotoVariants.All)
            {
                await _store.DeleteAsync(ObjectKey.ForReviewCandidate(claim.Generation, kind), budget.Token);
                budget.Token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OutOfMemoryException)
        {
            LogRetry(_logger, claim.Attempts, exception.GetType().Name);
            await RetryAsync(claim, cancellationToken);
            return 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!await CompleteSweepAsync(claim, cancellationToken))
        {
            LogLeaseLost(_logger);
        }

        return 1;
    }

    internal async Task<ExpiredCandidateCleanupClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var token = Guid.NewGuid();
        await using var command = Command(connection, $"""
            with picked as materialized (
              select c.generation from media.review_candidates c
              where {ExpiredObligation} and c.cleanup_requested_at<=$1 and c.cleanup_available_at<=$1
                and (c.cleanup_lease_until is null or c.cleanup_lease_until<=$1)
              order by c.cleanup_available_at,c.generation limit 1 for update of c skip locked)
            update media.review_candidates c
            set cleanup_token=$2,cleanup_lease_until=$3,
              cleanup_attempts=case when cleanup_attempts<1000000 then cleanup_attempts+1 else cleanup_attempts end
            from picked where c.generation=picked.generation
            returning c.generation,c.cleanup_attempts
            """, now, token, now + LeaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ExpiredCandidateCleanupClaim(reader.GetGuid(0), token, reader.GetInt32(1))
            : null;
    }

    internal async Task<bool> CompleteSweepAsync(ExpiredCandidateCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockCandidateAsync(connection, transaction, claim.Generation, cancellationToken))
        {
            return false;
        }

        var now = _time.GetUtcNow();
        // The sweep's timestamps come from the database clock: the tombstone purge counts its backup window from
        // deleted_at (ADR-0035, P3).
        await using var command = Command(connection, $"""
            update media.review_candidates c
            set last_cleanup_at=statement_timestamp(),deleted_at=statement_timestamp(),cleanup_token=null,cleanup_lease_until=null
            where c.generation=$1 and c.cleanup_token=$2 and c.cleanup_lease_until>$3
              and c.cleanup_requested_at<=$3 and {ExpiredObligation}
            """, claim.Generation, claim.Token, now);
        command.Transaction = transaction;
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    internal async Task<bool> RetryAsync(ExpiredCandidateCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockCandidateAsync(connection, transaction, claim.Generation, cancellationToken))
        {
            return false;
        }

        var now = _time.GetUtcNow();
        var delay = TimeSpan.FromSeconds(Math.Min(900, 15 * (1 << Math.Min(claim.Attempts - 1, 6))));
        await using var command = Command(connection, $"""
            update media.review_candidates c
            set cleanup_available_at=$4,cleanup_token=null,cleanup_lease_until=null
            where c.generation=$1 and c.cleanup_token=$2 and c.cleanup_lease_until>$3
              and c.cleanup_requested_at<=$3 and {ExpiredObligation}
            """, claim.Generation, claim.Token, now, now + delay);
        command.Transaction = transaction;
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private static async Task<bool> LockCandidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid generation, CancellationToken cancellationToken)
    {
        await using var command = Command(connection,
            "select generation from media.review_candidates where generation=$1 for update", generation);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Expired candidate cleanup failed on attempt {Attempt} ({Cause}); durable retry retained")]
    private static partial void LogRetry(ILogger logger, int attempt, string cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Expired candidate cleanup lost its lease; persisted ownership protects recovery")]
    private static partial void LogLeaseLost(ILogger logger);
}
