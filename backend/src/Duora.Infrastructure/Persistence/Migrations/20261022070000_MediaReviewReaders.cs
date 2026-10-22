using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>ADR-0035/M1: expansão antes de escritores; nenhum dado antigo recebe decisão ou caso.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261022070000_MediaReviewReaders")]
public sealed class MediaReviewReaders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table media.uploads drop constraint ck_uploads_state;
        alter table media.uploads add constraint ck_uploads_state
          check (state in ('Pending', 'Quarantined', 'Processing', 'Approved', 'Rejected', 'Deleted', 'AwaitingReview'));
        alter table media.uploads drop constraint ck_uploads_rejection_code;
        alter table media.uploads add constraint ck_uploads_rejection_code check (
          (rejection_code is null or rejection_code in ('UNSUPPORTED_FORMAT', 'TYPE_MISMATCH', 'TOO_MANY_PIXELS',
            'ANIMATED', 'CORRUPT_IMAGE', 'PROCESSING_LIMIT', 'PHOTO_REVIEW_REJECTED', 'PHOTO_REVIEW_EXPIRED',
            'PHOTO_REVIEW_UNAVAILABLE'))
          and (state <> 'Rejected' or rejection_code is not null)
          and (rejection_code is null or state in ('Rejected', 'Deleted')));
        drop index media.ix_uploads_pending_owner;
        create index ix_uploads_pending_owner on media.uploads (owner_id)
          where state in ('Pending', 'Quarantined', 'Processing', 'AwaitingReview');

        -- Minimal Safety-owned root. M2 will write it atomically with AwaitingReview; M3 adds decisions/appeals.
        -- SQL-managed like other additive operational tables; no runtime repository or seed in M1.
        create table safety.photo_review_cases (
          id uuid constraint pk_photo_review_cases primary key,
          upload_id uuid not null,
          candidate_generation uuid not null,
          policy_version varchar(64) not null,
          awaiting_review_at timestamptz not null,
          review_deadline timestamptz not null,
          version bigint not null,
          constraint ux_photo_review_cases_upload unique (upload_id),
          constraint fk_photo_review_cases_upload foreign key (upload_id) references media.uploads(id) on delete restrict,
          constraint ck_photo_review_cases_policy check (policy_version ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
          constraint ck_photo_review_cases_deadline check (isfinite(awaiting_review_at)
            and review_deadline = awaiting_review_at + interval '72 hours'),
          constraint ck_photo_review_cases_version check (version >= 1));
        create index ix_photo_review_cases_deadline on safety.photo_review_cases (review_deadline, id);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve review states and cases. Stop writers/consumers; use a proven compatible reader release or a reviewed forward fix.");
}
