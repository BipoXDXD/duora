using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Private preparation records start empty; no legacy photo or file is enrolled for cleanup.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261022150000_PrivateReviewCandidates")]
public sealed class PrivateReviewCandidates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table media.review_candidates (
          generation uuid primary key check (generation <> '00000000-0000-0000-0000-000000000000'),
          upload_id uuid not null references media.uploads(id) on delete restrict,
          upload_version bigint not null check (upload_version >= 1),
          detected_type varchar(32) not null check (detected_type in ('image/jpeg','image/png','image/webp')),
          width integer not null check (width > 0), height integer not null check (height > 0),
          created_at timestamptz not null, prepare_deadline timestamptz not null,
          committed_at timestamptz null,
          case_id uuid null references safety.photo_review_cases(id) on delete restrict,
          retention_until timestamptz null,
          cleanup_requested_at timestamptz null, writer_settled_at timestamptz null,
          last_cleanup_at timestamptz null, deleted_at timestamptz null,
          cleanup_token uuid null, cleanup_lease_until timestamptz null,
          cleanup_available_at timestamptz not null,
          cleanup_attempts integer not null default 0 check (cleanup_attempts >= 0),
          unique(upload_id,upload_version),
          check (width::bigint*height <= 20000000),
          check (isfinite(created_at) and isfinite(prepare_deadline) and prepare_deadline > created_at),
          check (isfinite(cleanup_available_at)),
          check (committed_at is null or isfinite(committed_at)),
          check (retention_until is null or (isfinite(retention_until) and retention_until > committed_at)),
          check (cleanup_requested_at is null or isfinite(cleanup_requested_at)),
          check (writer_settled_at is null or isfinite(writer_settled_at)),
          check (last_cleanup_at is null or isfinite(last_cleanup_at)),
          check (deleted_at is null or isfinite(deleted_at)),
          check (cleanup_lease_until is null or isfinite(cleanup_lease_until)),
          check ((committed_at is null) = (case_id is null)),
          check ((committed_at is null) = (retention_until is null)),
          check (committed_at is null or writer_settled_at is not null),
          check ((cleanup_token is null) = (cleanup_lease_until is null)),
          check (cleanup_token is null or cleanup_requested_at is not null),
          check (deleted_at is null or (cleanup_requested_at is not null and writer_settled_at is not null
            and cleanup_token is null)));
        create index ix_review_candidates_cleanup on media.review_candidates(cleanup_available_at,generation)
          where committed_at is null and deleted_at is null;

        create table media.review_candidate_variants (
          generation uuid not null references media.review_candidates(generation) on delete restrict,
          kind varchar(8) not null check (kind in ('thumb','card','full')),
          object_key varchar(128) not null unique,
          width integer not null check (width > 0), height integer not null check (height > 0),
          size_bytes bigint not null check (size_bytes between 1 and 16777216),
          sha256 bytea not null check (octet_length(sha256)=32),
          primary key(generation,kind),
          check (object_key='review-candidates/' || replace(generation::text,'-','') || '-' || kind),
          check (greatest(width,height) <= case kind when 'thumb' then 256 when 'card' then 768 else 1600 end));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve candidate generations, cases and cleanup obligations; use a reviewed compatible forward change.");
}
