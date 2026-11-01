using Npgsql;

namespace Duora.Infrastructure.Operations;

public sealed partial class RetentionService
{
    private static async Task ObserveOutboxAsync(NpgsqlConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        // Fixed dimensions both bound cardinality and emit zeros after the final row disappears.
        // Unknown consumers remain visible as 'other', never as arbitrary database-provided metric labels.
        await using var command = Command(connection, """
            with consumers(consumer) as (values
              ('transactional-email'),('media-processing'),('age-verification-provider'),
              ('selfie-provider'),('selfie-attempts-suspension'),('other')),
            states(state) as (values
              ('ready'),('scheduled'),('awaiting_other_measure'),('in_flight'),('lease_expired'),('failed')),
            classified as (
              select coalesce(c.consumer,'other') as consumer,
                case
                  when d.status='Failed' then 'failed'
                  when d.status='InFlight' and d.lease_expires_at<=$1 then 'lease_expired'
                  when d.status='InFlight' then 'in_flight'
                  when d.available_at<=$1 then 'ready'
                  when d.consumer='selfie-attempts-suspension' and d.last_error='AwaitingOtherMeasure'
                    then 'awaiting_other_measure'
                  else 'scheduled'
                end as state,
                e.occurred_at,
                case
                  when d.status='Pending' and d.available_at<=$1 then d.available_at
                  when d.status='InFlight' and d.lease_expires_at<=$1 then d.lease_expires_at
                end as eligible_at
              from ops.outbox_deliveries d
              join ops.outbox_events e on e.id=d.outbox_event_id
              left join consumers c on c.consumer=d.consumer
              where d.status in ('Pending','InFlight','Failed')),
            totals as (
              select consumer,state,count(*) as rows,min(occurred_at) as oldest,min(eligible_at) as eligible_at
              from classified group by consumer,state)
            select c.consumer,s.state,coalesce(t.rows,0),
              greatest(0,coalesce(extract(epoch from ($1-t.oldest)),0))::double precision,
              greatest(0,coalesce(extract(epoch from ($1-t.eligible_at)),0))::double precision
            from consumers c cross join states s left join totals t on t.consumer=c.consumer and t.state=s.state
            """, now);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var consumer = new KeyValuePair<string, object?>("consumer", reader.GetString(0));
            var state = new KeyValuePair<string, object?>("state", reader.GetString(1));
            OutboxRows.Record(reader.GetInt64(2), consumer, state);
            OutboxOldest.Record(reader.GetDouble(3), consumer, state);
            OutboxLag.Record(reader.GetDouble(4), consumer, state);
        }
    }
}
