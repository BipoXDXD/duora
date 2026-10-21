using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DuoraDbContext))]
[Migration("20261021150000_OperationalRetention")]
public sealed class OperationalRetention : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table ops.jobs drop constraint jobs_kind_check;
        alter table ops.jobs add constraint jobs_kind_check check(kind in ('build-round','close-round','expire-invite','game-deadline','retention'));
        alter table ops.jobs add column retention_cutoff timestamptz;
        create unique index ux_jobs_retention on ops.jobs(kind) where kind='retention';
        create index ix_jobs_retention_completed on ops.jobs(completed_at,id) where status='Done' and kind<>'retention';
        alter table ops.outbox_deliveries add column reviewed_at timestamptz;
        alter table ops.outbox_deliveries add constraint ck_outbox_reviewed_failure check(reviewed_at is null or (status='Failed' and completed_at is not null));
        create index ix_outbox_deliveries_retention on ops.outbox_deliveries(completed_at,id)
          where status in ('Delivered','Expired','Failed');
        create index ix_outbox_events_retention on ops.outbox_events(occurred_at,id);
        create index ix_webhook_receipts_retention on ops.webhook_receipts(processed_at,id)
          where state in ('Processed','Rejected');
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Stop retention before binary rollback; preserve the additive schema and review evidence. Deleted rows require approved backup recovery.");
}
