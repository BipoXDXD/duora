using Duora.Core.Media;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.Infrastructure.Media;

internal sealed record DecidedCandidateCleanupClaim(Guid Generation, Guid Token, int Attempts);

/// <summary>
/// Consumes only the private-candidate cleanup obligation committed with a human decision: the reviewer's approval
/// (the final derivatives already exist) or rejection (no appeal exists, owner decision of 2026-10-28; a new upload
/// starts a new review), or the owner's deletion of the photo (API-M03), explained by its tombstone.
/// </summary>
public sealed partial class DecidedCandidateCleanupDispatcher
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DeleteBudget = TimeSpan.FromSeconds(15);
    private readonly NpgsqlDataSource _source;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<DecidedCandidateCleanupDispatcher> _logger;

    // No enrollment by file age or upload state. An immutable decision or tombstone must explain this exact marker; a
    // deletion also takes over a marker another obligation set before it, since that obligation reads the photo state.
    private const string DecidedObligation = """
        c.committed_at is not null and c.writer_settled_at is not null
        and c.deleted_at is null and c.cleanup_requested_at is not null
        and (exists (
          select 1 from media.uploads u join media.photo_tombstones t on t.photo_id=u.id
          where u.id=c.upload_id and u.state='Deleted' and t.owner_id=u.owner_id
            and c.cleanup_requested_at<=t.deleted_at)
        or exists (
          select 1 from safety.photo_review_decisions d
          join safety.photo_review_cases r on r.id=d.case_id
          where d.case_id=c.case_id and d.candidate_generation=c.generation
            and r.candidate_generation=c.generation and r.upload_id=c.upload_id
            and d.appeal_id is null and d.policy_version=r.policy_version
            and ((d.decision='approve' and d.reason_code='meets_policy') or (d.decision='reject' and d.reason_code='does_not_meet_policy'))
            and d.decided_at=c.cleanup_requested_at and d.candidate_retention_until_snapshot=c.retention_until))
        """;

    internal DecidedCandidateCleanupDispatcher(NpgsqlDataSource source, IObjectStore store,
        TimeProvider time, bool enabled, ILogger<DecidedCandidateCleanupDispatcher> logger)
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

    internal async Task<DecidedCandidateCleanupClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var token = Guid.NewGuid();
        await using var command = Command(connection, $"""
            with picked as materialized (
              select c.generation from media.review_candidates c
              where {DecidedObligation}
                and c.cleanup_requested_at<=$1 and c.cleanup_available_at<=$1
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
            ? new DecidedCandidateCleanupClaim(reader.GetGuid(0), token, reader.GetInt32(1))
            : null;
    }

    internal async Task<bool> CompleteSweepAsync(DecidedCandidateCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
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
              and c.cleanup_requested_at<=$3 and {DecidedObligation}
            """, claim.Generation, claim.Token, now);
        command.Transaction = transaction;
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    internal async Task<bool> RetryAsync(DecidedCandidateCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
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
              and c.cleanup_requested_at<=$3 and {DecidedObligation}
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Decided candidate cleanup failed on attempt {Attempt} ({Cause}); durable retry retained")]
    private static partial void LogRetry(ILogger logger, int attempt, string cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Decided candidate cleanup lost its lease; persisted ownership protects recovery")]
    private static partial void LogLeaseLost(ILogger logger);
}
