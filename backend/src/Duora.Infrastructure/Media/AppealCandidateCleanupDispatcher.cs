using Duora.Core.Media;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.Infrastructure.Media;

internal sealed record AppealCandidateCleanupClaim(Guid Generation, Guid Token, int Attempts);

/// <summary>Consumes only the durable obligation of a closed appeal, preserving original facts and final derivatives.</summary>
public sealed partial class AppealCandidateCleanupDispatcher
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DeleteBudget = TimeSpan.FromSeconds(15);
    private readonly NpgsqlDataSource _source;
    private readonly IObjectStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<AppealCandidateCleanupDispatcher> _logger;

    // No enrollment by file age or upload state. The immutable decision must explain this exact marker.
    private const string AppealObligation = """
        c.committed_at is not null and c.writer_settled_at is not null
        and c.deleted_at is null and c.cleanup_requested_at is not null
        and exists (
          select 1 from safety.photo_review_appeals a
          join safety.photo_review_cases r on r.id=a.case_id
          join media.uploads u on u.id=r.upload_id
          join safety.photo_review_decisions original on original.id=a.original_decision_id
          where a.case_id=c.case_id and a.owner_id=u.owner_id and a.candidate_generation=c.generation
            and a.policy_version=r.policy_version and r.candidate_generation=c.generation and r.upload_id=c.upload_id
            and a.policy_version='human-review-v1' and original.case_id=r.id and original.candidate_generation=c.generation
            and original.policy_version=r.policy_version and original.appeal_id is null
            and original.decision='reject' and original.reason_code='does_not_meet_policy'
            and a.original_rejected_at=original.decided_at and a.maximum_retention_until=original.decided_at+interval '720 hours'
            and a.closed_at is not null and a.closed_at=c.cleanup_requested_at
            and c.upload_version=u.version-4
            and (
              (a.status in ('ClosedApproved','ClosedRejected') and c.cleanup_audit_id is null
                and exists(select 1 from safety.photo_review_decisions d
                  join safety.photo_review_appeal_decision_commands receipt on receipt.decision_id=d.id
                  join ops.audit_events e on e.target_id=d.id
                  where d.appeal_id=a.id and d.original_decision_id=original.id
                    and d.case_id=r.id and d.case_version=r.version and d.candidate_generation=c.generation
                    and d.policy_version=r.policy_version and d.actor_id<>original.actor_id
                    and d.decided_at=a.closed_at and d.decided_at<a.maximum_retention_until
                    and original.case_version=r.version-2
                    and d.candidate_retention_until_snapshot=least(d.decided_at+interval '24 hours',a.maximum_retention_until)
                    and c.retention_until=d.candidate_retention_until_snapshot
                    and receipt.appeal_id=a.id and receipt.case_id=r.id and receipt.actor_id=d.actor_id
                    and receipt.result_appeal_version=a.version and receipt.result_case_version=r.version
                    and receipt.result_photo_version=u.version and receipt.result_decision=d.decision
                    and e.actor_kind='Moderator' and e.actor_id=d.actor_id and e.target_type='photo_review_decision'
                    and e.target_version=d.case_version
                    and ((a.status='ClosedApproved' and d.decision='approve' and d.reason_code='meets_policy'
                      and e.action='safety.photo_review_appeal_approved' and u.state='Approved' and u.rejection_code is null
                      and exists(select 1 from media.media_assets asset join media.review_promotions p on p.asset_id=asset.id
                        where asset.id=receipt.result_asset_id and asset.upload_id=u.id and asset.owner_id=u.owner_id
                          and asset.state='Approved' and asset.deleted_at is null and asset.approved_at=d.decided_at
                          and p.appeal_id=a.id and p.appeal_version=a.version-1
                          and p.committed_at=d.decided_at and p.writer_settled_at is not null
                          and p.candidate_generation=c.generation and p.upload_version=u.version-1 and p.case_version=r.version-1))
                    or (a.status='ClosedRejected' and d.decision='reject' and d.reason_code='does_not_meet_policy'
                      and e.action='safety.photo_review_appeal_rejected' and u.state='Rejected'
                      and u.rejection_code='PHOTO_REVIEW_REJECTED' and receipt.result_asset_id is null))))
              or (a.status='ClosedUnavailable' and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_REJECTED'
                and original.case_version=r.version-2
                and c.retention_until=least(greatest(a.closed_at,c.committed_at+interval '1 microsecond'),a.maximum_retention_until)
                and not exists(select 1 from safety.photo_review_decisions d where d.appeal_id=a.id)
                and exists(select 1 from ops.audit_events e where e.id=c.cleanup_audit_id
                  and e.actor_kind='System' and e.actor_id is null and e.action='safety.photo_review_appeal_unavailable'
                  and e.target_type='photo_review_appeal' and e.target_id=a.id and e.target_version=a.version))))
        """;

    internal AppealCandidateCleanupDispatcher(NpgsqlDataSource source, IObjectStore store,
        TimeProvider time, bool enabled, ILogger<AppealCandidateCleanupDispatcher> logger)
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

    internal async Task<AppealCandidateCleanupClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var token = Guid.NewGuid();
        await using var command = Command(connection, $"""
            with picked as materialized (
              select c.generation from media.review_candidates c
              where {AppealObligation}
                and c.retention_until<=$1 and c.cleanup_requested_at<=$1 and c.cleanup_available_at<=$1
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
            ? new AppealCandidateCleanupClaim(reader.GetGuid(0), token, reader.GetInt32(1))
            : null;
    }

    internal async Task<bool> CompleteSweepAsync(AppealCandidateCleanupClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockCandidateAsync(connection, transaction, claim.Generation, cancellationToken))
        {
            return false;
        }

        var now = _time.GetUtcNow();
        await using var command = Command(connection, $"""
            update media.review_candidates c
            set last_cleanup_at=$3,deleted_at=$3,cleanup_token=null,cleanup_lease_until=null
            where c.generation=$1 and c.cleanup_token=$2 and c.cleanup_lease_until>$3
              and c.retention_until<=$3 and c.cleanup_requested_at<=$3 and {AppealObligation}
            """, claim.Generation, claim.Token, now);
        command.Transaction = transaction;
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    internal async Task<bool> RetryAsync(AppealCandidateCleanupClaim claim, CancellationToken cancellationToken)
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
              and c.retention_until<=$3 and c.cleanup_requested_at<=$3 and {AppealObligation}
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Appeal candidate cleanup failed on attempt {Attempt} ({Cause}); durable retry retained")]
    private static partial void LogRetry(ILogger logger, int attempt, string cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Appeal candidate cleanup lost its lease; persisted ownership protects recovery")]
    private static partial void LogLeaseLost(ILogger logger);
}
