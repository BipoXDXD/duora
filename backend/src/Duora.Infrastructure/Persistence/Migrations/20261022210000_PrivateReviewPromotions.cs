using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Empty generation-owned preparations; no existing photo or storage key is enrolled.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261022210000_PrivateReviewPromotions")]
public sealed class PrivateReviewPromotions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table media.review_promotions (
          generation uuid primary key check (generation <> '00000000-0000-0000-0000-000000000000'),
          upload_id uuid not null references media.uploads(id) on delete restrict,
          case_id uuid not null references safety.photo_review_cases(id) on delete restrict,
          case_version bigint not null check (case_version >= 1),
          upload_version bigint not null check (upload_version >= 1),
          candidate_generation uuid not null references media.review_candidates(generation) on delete restrict,
          policy_version varchar(64) not null check (policy_version ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
          detected_type varchar(32) not null check (detected_type in ('image/jpeg','image/png','image/webp')),
          width integer not null check (width > 0), height integer not null check (height > 0),
          created_at timestamptz not null, prepare_deadline timestamptz not null,
          writer_settled_at timestamptz null, committed_at timestamptz null,
          asset_id uuid null unique references media.media_assets(id) on delete restrict,
          cleanup_requested_at timestamptz null, last_cleanup_at timestamptz null, deleted_at timestamptz null,
          cleanup_token uuid null check (cleanup_token <> '00000000-0000-0000-0000-000000000000'),
          cleanup_lease_until timestamptz null, cleanup_available_at timestamptz not null,
          cleanup_attempts integer not null default 0 check (cleanup_attempts >= 0),
          check (width::bigint*height <= 20000000),
          check (isfinite(created_at) and isfinite(prepare_deadline)
            and prepare_deadline>created_at and prepare_deadline<=created_at+interval '15 seconds'),
          check (writer_settled_at is null or (isfinite(writer_settled_at) and writer_settled_at>=created_at)),
          check (committed_at is null or isfinite(committed_at)),
          check ((committed_at is null) = (asset_id is null)),
          check (committed_at is null or (writer_settled_at is not null
            and cleanup_requested_at is null and cleanup_token is null and deleted_at is null)),
          check (cleanup_requested_at is null or isfinite(cleanup_requested_at)),
          check (last_cleanup_at is null or isfinite(last_cleanup_at)),
          check (deleted_at is null or isfinite(deleted_at)),
          check (isfinite(cleanup_available_at)),
          check (cleanup_lease_until is null or isfinite(cleanup_lease_until)),
          check ((cleanup_token is null) = (cleanup_lease_until is null)),
          check (cleanup_token is null or cleanup_requested_at is not null),
          check (deleted_at is null or (committed_at is null and cleanup_requested_at is not null
            and writer_settled_at is not null and cleanup_token is null)));
        create index ix_review_promotions_cleanup on media.review_promotions(cleanup_available_at,generation)
          where committed_at is null and deleted_at is null;

        create table media.review_promotion_variants (
          generation uuid not null references media.review_promotions(generation) on delete restrict,
          kind varchar(8) not null check (kind in ('thumb','card','full')),
          object_key varchar(128) not null unique,
          width integer not null check (width > 0), height integer not null check (height > 0),
          size_bytes bigint not null check (size_bytes between 1 and 16777216),
          sha256 bytea not null check (octet_length(sha256)=32),
          primary key(generation,kind),
          check (object_key='derivatives/review-' || replace(generation::text,'-','') || '-' || kind),
          check (greatest(width,height) <= case kind when 'thumb' then 256 when 'card' then 768 else 1600 end));
        do $$ begin
          if exists(select 1 from pg_roles where rolname='duora_app') then
            revoke update,delete,truncate on media.review_promotion_variants from duora_app;
          end if;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve promotion generations and cleanup obligations; use a reviewed compatible forward change.");
}
