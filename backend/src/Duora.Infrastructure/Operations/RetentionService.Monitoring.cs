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
    private static readonly Gauge<double> Oldest = Meter.CreateGauge<double>("duora.retention.oldest_seconds", "s",
        "Seconds overdue for deletion under the retention policy; zero when no row is eligible.");
    private static readonly Gauge<long> Unreviewed = Meter.CreateGauge<long>("duora.retention.unreviewed_failures_over_30_days");
    private static readonly Gauge<long> FailedJobs = Meter.CreateGauge<long>("duora.retention.failed_jobs");

    private static readonly Gauge<long> OutboxRows = Meter.CreateGauge<long>("duora.outbox.rows",
        description: "Outstanding deliveries by bounded consumer and monitoring state, including terminal failures.");
    private static readonly Gauge<double> OutboxOldest = Meter.CreateGauge<double>("duora.outbox.oldest_seconds", "s",
        "Age since event occurrence; inventory age, not processing lag.");
    private static readonly Gauge<double> OutboxLag = Meter.CreateGauge<double>("duora.outbox.processing_lag_seconds", "s",
        "Seconds since next claim eligibility for ready deliveries or expired leases; zero in other states.");

    public async Task<RetentionHealth> ObserveAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var now = time.GetUtcNow();
        // Match RetentionService.Sql.cs eligibility, without taking cleanup locks or simulating future deletes.
        // Inventory includes protected/pending rows; lag starts only after the applicable retention window.
        await using var command = Command(connection, """
            select 'outbox_events',count(*),coalesce(extract(epoch from ($1-interval '7 days'-
              min(occurred_at) filter(where occurred_at<$1-interval '7 days'
                and not exists(select 1 from ops.outbox_deliveries d where d.outbox_event_id=e.id)))),0)::double precision
              from ops.outbox_events e
            union all
            select 'outbox_deliveries',count(*),coalesce(extract(epoch from ($1-min(case
              when status in ('Delivered','Expired') and completed_at<$1-interval '7 days' then completed_at+interval '7 days'
              when status='Failed' and reviewed_at is not null and completed_at<$1-interval '30 days' then completed_at+interval '30 days'
              end))),0)::double precision from ops.outbox_deliveries
            union all
            select 'webhook_receipts',count(*),coalesce(extract(epoch from ($1-interval '30 days'-
              min(processed_at) filter(where state in ('Processed','Rejected') and processed_at<$1-interval '30 days'))),0)::double precision
              from ops.webhook_receipts
            union all
            select 'jobs',count(*),coalesce(extract(epoch from ($1-interval '7 days'-
              min(completed_at) filter(where kind<>'retention' and status='Done' and completed_at<$1-interval '7 days'))),0)::double precision
              from ops.jobs
            """, now);
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
        await ObserveOutboxAsync(connection, now, ct);
        await using var failures = Command(connection, """
            select (select count(*) from ops.outbox_deliveries where status='Failed' and reviewed_at is null and completed_at<$1-interval '30 days'),
              (select count(*) from ops.jobs where kind='retention' and status='Failed')
            """, now);
        await using var counts = await failures.ExecuteReaderAsync(ct);
        await counts.ReadAsync(ct);
        var unreviewed = counts.GetInt64(0);
        var failed = counts.GetInt64(1);
        Unreviewed.Record(unreviewed);
        FailedJobs.Record(failed);
        return new(tables, unreviewed, failed);
    }
}
