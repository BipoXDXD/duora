using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Empty immutable access history; contains no image, object key, owner identity or session ticket.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261022190000_PhotoReviewEvidenceAccesses")]
public sealed class PhotoReviewEvidenceAccesses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table safety.photo_review_evidence_accesses (
          id uuid primary key check (id <> '00000000-0000-0000-0000-000000000000'),
          case_id uuid not null references safety.photo_review_cases(id) on delete restrict,
          case_version bigint not null check (case_version >= 2),
          actor_id uuid not null references identity.users(id) on delete restrict,
          candidate_generation uuid not null references media.review_candidates(generation) on delete restrict,
          policy_version varchar(64) not null check (policy_version ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
          variant varchar(8) not null check (variant in ('thumb','card','full')),
          variant_sha256 bytea not null check (octet_length(variant_sha256)=32),
          occurred_at timestamptz not null check (isfinite(occurred_at)));
        create index ix_photo_review_evidence_access_case on safety.photo_review_evidence_accesses(case_id,occurred_at,id);
        do $$ begin
          if exists(select 1 from pg_roles where rolname='duora_app') then
            revoke update,delete,truncate on safety.photo_review_evidence_accesses from duora_app;
          end if;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve minimal evidence access and audit history; use a reviewed compatible forward change.");
}
