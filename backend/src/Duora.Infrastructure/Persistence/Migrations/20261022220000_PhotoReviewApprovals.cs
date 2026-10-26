using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Compatible approval expansion; preserves all rejection history, receipts and append-only grants.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261022220000_PhotoReviewApprovals")]
public sealed class PhotoReviewApprovals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table safety.photo_review_decisions
          drop constraint photo_review_decisions_decision_check,
          drop constraint photo_review_decisions_reason_code_check,
          drop constraint photo_review_decisions_check;
        alter table safety.photo_review_decisions
          add constraint ck_photo_review_decisions_outcome
            check ((decision='approve' and reason_code='meets_policy')
              or (decision='reject' and reason_code='does_not_meet_policy')),
          add constraint ck_photo_review_decisions_candidate_retention
            check (isfinite(candidate_retention_until_snapshot)
              and candidate_retention_until_snapshot=decided_at+
                case decision when 'approve' then interval '24 hours' else interval '168 hours' end);
        alter table safety.photo_review_decision_commands
          add column result_decision varchar(8) not null default 'reject',
          add column result_asset_id uuid null,
          add constraint ck_photo_review_commands_result
            check ((result_decision='reject' and result_asset_id is null)
              or (result_decision='approve' and result_asset_id is not null
                and result_asset_id<>'00000000-0000-0000-0000-000000000000'));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve confirmed approvals and receipts; use a reviewed compatible forward change.");
}
