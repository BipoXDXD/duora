using System.Diagnostics;
using Duora.Infrastructure.Audit;
using Npgsql;

namespace Duora.Infrastructure.Operations;

public enum FailureResolution { Resolved, AcceptedLoss }
public sealed record RetentionJob(Guid Id, long Token, DateTimeOffset Cutoff);

/// <summary>One durable recurring job. Every database batch is fenced and commits with its next schedule.</summary>
public sealed partial class RetentionService(NpgsqlDataSource source, TimeProvider time)
{
    private const int BatchSize = 1000;
    private static readonly Guid Aggregate = new("00000000-0000-7000-8000-000000000046");

    public async Task<RetentionJob?> ClaimAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var now = time.GetUtcNow();
        await Execute(connection, """
            insert into ops.jobs(id,kind,aggregate_id,due_at,available_at)
            values($1,'retention',$2,$3,$3) on conflict do nothing
            """, ct, Guid.CreateVersion7(), Aggregate, now);
        await using var command = Command(connection, """
            with picked as (
              select id from ops.jobs where kind='retention' and
                ((status='Pending' and available_at<=$1) or (status='InFlight' and lease_expires_at<=$1))
              for update skip locked limit 1)
            update ops.jobs j set fencing_token=fencing_token+1,attempts=attempts+1,
              status=case when attempts>=8 then 'Failed' else 'InFlight' end,
              lease_expires_at=case when attempts>=8 then null else $1+interval '60 seconds' end,
              completed_at=case when attempts>=8 then $1 else null end,
              last_error_code=case when attempts>=8 then 'LeaseExpiredExhausted' else null end,
              retention_cutoff=coalesce(retention_cutoff,$1)
            from picked where j.id=picked.id returning j.id,j.fencing_token,j.retention_cutoff,j.status
            """, now);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && reader.GetString(3) == "InFlight"
            ? new(reader.GetGuid(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2)) : null;
    }

    public async Task<bool> ExecuteAsync(RetentionJob job, CancellationToken ct)
    {
        // Real elapsed ceiling, independent from the injected wall clock. No external work inside this lease.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        ct = budget.Token;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await Execute(connection, "set local lock_timeout='500ms'; set local statement_timeout='2s'", ct);
        await using var ownership = Command(connection, """
            select 1 from ops.jobs where id=$1 and kind='retention' and status='InFlight'
              and fencing_token=$2 and lease_expires_at>$3 and retention_cutoff=$4 for update
            """, job.Id, job.Token, time.GetUtcNow(), job.Cutoff);
        if (await ownership.ExecuteScalarAsync(ct) is null)
        {
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        var deliveries = await Execute(connection, DeleteDeliveries, ct, job.Cutoff);
        var receipts = await Execute(connection, DeleteReceipts, ct, job.Cutoff);
        var (events, fullEventBatch) = await DeleteOrphansAsync(connection, job.Cutoff, ct);
        var jobs = await Execute(connection, DeleteJobs, ct, job.Cutoff);
        var more = deliveries == BatchSize || receipts == BatchSize || fullEventBatch || jobs == BatchSize;
        foreach (var (table, deleted) in new[] { ("deliveries", deliveries), ("receipts", receipts), ("events", events), ("jobs", jobs) })
        {
            if (deleted > 0)
            {
                await Audit(connection, "System", null, $"ops.retention_{table}_deleted", job.Id, deleted, ct);
            }
        }
        var now = time.GetUtcNow();
        var owned = await Execute(connection, """
            update ops.jobs set status='Pending',attempts=0,lease_expires_at=null,completed_at=null,last_error_code=null,
              available_at=$3,retention_cutoff=case when $4 then retention_cutoff else null end
            where id=$1 and kind='retention' and fencing_token=$2 and status='InFlight' and lease_expires_at>$5
            """, ct, job.Id, job.Token, more ? now : now.AddDays(1), more, now);
        if (owned != 1)
        {
            return false; // Disposing the transaction rolls back every deletion and audit row.
        }
        await tx.CommitAsync(ct);
        foreach (var (table, deleted) in new[] { ("deliveries", deliveries), ("receipts", receipts), ("events", events), ("jobs", jobs) })
        {
            Deleted.Add(deleted, new KeyValuePair<string, object?>("table", table));
        }
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        return true;
    }

    private static async Task<(int Deleted, bool FullBatch)> DeleteOrphansAsync(NpgsqlConnection connection, DateTimeOffset cutoff, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using (var command = Command(connection, LockOrphanCandidates, cutoff))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetGuid(0));
            }
        }
        // READ COMMITTED creates a fresh snapshot after candidate locks; FK insertions now wait for commit.
        return ids.Count == 0 ? (0, false) : (await Execute(connection, DeleteLockedOrphans, ct, ids.ToArray()), ids.Count == BatchSize);
    }

    public async Task RetryAsync(RetentionJob job, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await Execute(connection, """
            update ops.jobs set status=case when attempts>=8 then 'Failed' else 'Pending' end,
              completed_at=case when attempts>=8 then $3 else null end,lease_expires_at=null,
              available_at=$3+interval '15 seconds',last_error_code='RetentionFailed'
            where id=$1 and kind='retention' and fencing_token=$2 and status='InFlight' and lease_expires_at>$3
            """, ct, job.Id, job.Token, time.GetUtcNow());
    }

    public async Task<bool> ReviewFailureAsync(Guid id, OperatorName actor, FailureResolution resolution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var action = resolution switch
        {
            FailureResolution.Resolved => "ops.outbox_failure_resolved",
            FailureResolution.AcceptedLoss => "ops.outbox_failure_loss_accepted",
            _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
        };
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var changed = await Execute(connection, """
            update ops.outbox_deliveries set reviewed_at=$2
            where id=$1 and status='Failed' and completed_at is not null and reviewed_at is null
            """, ct, id, time.GetUtcNow());
        if (changed == 0)
        {
            return false;
        }
        await Audit(connection, "TerminalOperator", actor.Value, action, id, null, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> ResumeAsync(OperatorName actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var cmd = Command(connection, """
            update ops.jobs set status='Pending',attempts=0,completed_at=null,last_error_code=null,available_at=$1
            where kind='retention' and status='Failed' returning id
            """, time.GetUtcNow());
        if (await cmd.ExecuteScalarAsync(ct) is not Guid id)
        {
            return false;
        }
        await Audit(connection, "TerminalOperator", actor.Value, "ops.retention_resumed", id, null, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private Task<int> Audit(NpgsqlConnection connection, string kind, string? actor, string action, Guid id, int? count, CancellationToken ct) =>
        Execute(connection, """
            insert into ops.audit_events(occurred_at,actor_kind,actor_label,action,target_type,target_id,target_version)
            values($1,$2,$3,$4,'retention',$5,$6)
            """, ct, time.GetUtcNow(), kind, actor, action, id, count);

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object?[] args)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 3 };
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }
        return command;
    }

    private static async Task<int> Execute(NpgsqlConnection connection, string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = Command(connection, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
