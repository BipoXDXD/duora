using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Closed appeal facts and receipts; initial decision retention and immutable history are preserved.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261025120000_PhotoReviewAppealDecisions")]
public sealed class PhotoReviewAppealDecisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table safety.photo_review_appeals
          drop constraint photo_review_appeals_status_check,
          drop constraint photo_review_appeals_closed_at_check;
        alter table safety.photo_review_appeals add constraint ck_photo_appeal_closed
          check ((status='Open' and closed_at is null)
            or (status in ('ClosedApproved','ClosedRejected') and closed_at is not null and isfinite(closed_at)
              and closed_at>=requested_at and closed_at<maximum_retention_until)
            or (status='ClosedUnavailable' and closed_at is not null and isfinite(closed_at) and closed_at>=requested_at));
        alter table safety.photo_review_decisions
          add column original_decision_id uuid null references safety.photo_review_decisions(id) on delete restrict,
          add constraint ck_photo_decision_appeal_origin check
            ((appeal_id is null and original_decision_id is null)
              or (appeal_id is not null and original_decision_id is not null and original_decision_id<>id)),
          drop constraint ck_photo_review_decisions_candidate_retention;
        alter table safety.photo_review_decisions add constraint ck_photo_review_decisions_candidate_retention check
          (isfinite(candidate_retention_until_snapshot) and
            ((appeal_id is null and candidate_retention_until_snapshot=decided_at+
              case decision when 'approve' then interval '24 hours' else interval '168 hours' end)
            or (appeal_id is not null and candidate_retention_until_snapshot>decided_at
              and candidate_retention_until_snapshot<=decided_at+interval '24 hours')));
        create unique index uq_photo_review_appeal_decision on safety.photo_review_decisions(appeal_id) where appeal_id is not null;
        alter table media.review_promotions
          add column appeal_id uuid null references safety.photo_review_appeals(id) on delete restrict,
          add column appeal_version bigint null,
          add constraint ck_review_promotion_appeal check ((appeal_id is null and appeal_version is null)
            or (appeal_id is not null and appeal_version is not null and appeal_version>=2));
        create table safety.photo_review_appeal_decision_commands (
          actor_id uuid not null references identity.users(id) on delete restrict,
          key_hash bytea not null check (octet_length(key_hash)=32),
          intent_sha256 bytea not null check (octet_length(intent_sha256)=32),
          appeal_id uuid not null references safety.photo_review_appeals(id) on delete restrict,
          decision_id uuid not null unique references safety.photo_review_decisions(id) on delete restrict,
          case_id uuid not null references safety.photo_review_cases(id) on delete restrict,
          result_appeal_version bigint not null check (result_appeal_version>=3),
          result_case_version bigint not null check (result_case_version>=3),
          result_photo_version bigint not null check (result_photo_version>=4),
          result_decision varchar(8) not null,
          result_asset_id uuid null references media.media_assets(id) on delete restrict,
          created_at timestamptz not null check (isfinite(created_at)),
          check ((result_decision='approve' and result_asset_id is not null)
            or (result_decision='reject' and result_asset_id is null)),
          primary key(actor_id,key_hash));
        do $$ begin
          if exists(select 1 from pg_roles where rolname='duora_app') then
            revoke update,delete,truncate on safety.photo_review_appeal_decision_commands from duora_app;
          end if;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve appeal decisions, original facts and receipts; use a reviewed compatible forward change.");
}
