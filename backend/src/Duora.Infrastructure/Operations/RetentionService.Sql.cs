namespace Duora.Infrastructure.Operations;

public sealed partial class RetentionService
{
    // Lock deliveries before events, like the dispatcher. SKIP LOCKED avoids waiting for a live dispatcher.
    private const string DeleteDeliveries = """
        with picked as (
          select id from ops.outbox_deliveries where
            (status in ('Delivered','Expired') and completed_at < $1-interval '7 days') or
            (status='Failed' and reviewed_at is not null and completed_at < $1-interval '30 days')
          order by completed_at,id for update skip locked limit 1000)
        delete from ops.outbox_deliveries d using picked where d.id=picked.id
        """;

    private const string DeleteReceipts = """
        with picked as (
          select id from ops.webhook_receipts where state in ('Processed','Rejected')
            and processed_at < $1-interval '30 days'
          order by processed_at,id for update skip locked limit 1000)
        delete from ops.webhook_receipts r using picked where r.id=picked.id
        """;

    // The first snapshot only chooses and locks candidates. A separate command must recheck orphans:
    // an FK insertion can commit between this snapshot and acquisition of the parent lock.
    private const string LockOrphanCandidates = """
        select e.id from ops.outbox_events e where e.occurred_at < $1-interval '7 days'
          and not exists(select 1 from ops.outbox_deliveries d where d.outbox_event_id=e.id)
        order by e.occurred_at,e.id for update of e skip locked limit 1000
        """;

    private const string DeleteLockedOrphans = """
        delete from ops.outbox_events e where e.id=any($1)
          and not exists(select 1 from ops.outbox_deliveries d where d.outbox_event_id=e.id)
        """;

    // Current participation schema has Done/Failed, no Expired/Cancelled jobs. Never remove the recurring identity.
    private const string DeleteJobs = """
        with picked as (
          select id from ops.jobs where kind<>'retention' and status='Done' and completed_at < $1-interval '7 days'
          order by completed_at,id for update skip locked limit 1000)
        delete from ops.jobs j using picked where j.id=picked.id
        """;
}
