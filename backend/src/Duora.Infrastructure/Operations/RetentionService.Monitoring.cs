using System.Diagnostics.Metrics;

namespace Duora.Infrastructure.Operations;

public sealed record RetentionTableHealth(string Table, long Rows, double OldestSeconds);
public sealed record RetentionHealth(IReadOnlyList<RetentionTableHealth> Tables, long UnreviewedFailures, long FailedJobs);

public sealed partial class RetentionService
{
    private static readonly Meter Meter = new("Duora.Retention");
    private static readonly Counter<long> Deleted = Meter.CreateCounter<long>("duora.retention.deleted");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("duora.retention.batch_seconds", "s");
    private static readonly Gauge<long> Rows = Meter.CreateGauge<long>("duora.retention.rows");
    private static readonly Gauge<double> Oldest = Meter.CreateGauge<double>("duora.retention.oldest_seconds", "s");
    private static readonly Gauge<long> Unreviewed = Meter.CreateGauge<long>("duora.retention.unreviewed_failures_over_30_days");
    private static readonly Gauge<long> FailedJobs = Meter.CreateGauge<long>("duora.retention.failed_jobs");

    public async Task<RetentionHealth> ObserveAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = Command(connection, """
            select 'outbox_events',count(*),coalesce(greatest(0,extract(epoch from ($1-min(occurred_at)))),0)::double precision from ops.outbox_events
            union all select 'outbox_deliveries',count(*),coalesce(greatest(0,extract(epoch from ($1-min(created_at)))),0)::double precision from ops.outbox_deliveries
            union all select 'webhook_receipts',count(*),coalesce(greatest(0,extract(epoch from ($1-min(received_at)))),0)::double precision from ops.webhook_receipts
            union all select 'jobs',count(*),coalesce(greatest(0,extract(epoch from ($1-min(available_at)))),0)::double precision from ops.jobs
            """, time.GetUtcNow());
        var tables = new List<RetentionTableHealth>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var table = new RetentionTableHealth(reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2));
                tables.Add(table);
                Rows.Record(table.Rows, new KeyValuePair<string, object?>("table", table.Table));
                Oldest.Record(table.OldestSeconds, new KeyValuePair<string, object?>("table", table.Table));
            }
        }
        await using var failures = Command(connection, """
            select (select count(*) from ops.outbox_deliveries where status='Failed' and reviewed_at is null and completed_at<$1-interval '30 days'),
              (select count(*) from ops.jobs where kind='retention' and status='Failed')
            """, time.GetUtcNow());
        await using var counts = await failures.ExecuteReaderAsync(ct);
        await counts.ReadAsync(ct);
        var unreviewed = counts.GetInt64(0);
        var failed = counts.GetInt64(1);
        Unreviewed.Record(unreviewed);
        FailedJobs.Record(failed);
        return new(tables, unreviewed, failed);
    }
}
