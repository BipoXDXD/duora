using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Additive private appeal access history; never enrolls or authorizes a real reviewer.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261025063000_PhotoReviewAppealEvidenceAccesses")]
public sealed class PhotoReviewAppealEvidenceAccesses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table safety.photo_review_appeal_assignments add constraint uq_appeal_assignment_access_binding
          unique(id,appeal_id,appeal_version,reviewer_id);
        create table safety.photo_review_appeal_evidence_accesses (
          id uuid primary key check (id <> '00000000-0000-0000-0000-000000000000'),
          appeal_id uuid not null references safety.photo_review_appeals(id) on delete restrict,
          appeal_version bigint not null check (appeal_version>=2),
          assignment_id uuid not null,
          actor_id uuid not null references identity.users(id) on delete restrict,
          original_decision_id uuid not null,
          candidate_generation uuid not null references media.review_candidates(generation) on delete restrict,
          policy_version varchar(64) not null check (policy_version ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
          variant varchar(8) not null check (variant in ('thumb','card','full')),
          variant_sha256 bytea not null check (octet_length(variant_sha256)=32),
          occurred_at timestamptz not null check (isfinite(occurred_at)),
          foreign key(assignment_id,appeal_id,appeal_version,actor_id)
            references safety.photo_review_appeal_assignments(id,appeal_id,appeal_version,reviewer_id) on delete restrict,
          foreign key(original_decision_id,variant)
            references safety.photo_review_decision_variants(decision_id,variant) on delete restrict);
        create index ix_appeal_evidence_access_root on safety.photo_review_appeal_evidence_accesses(appeal_id,occurred_at,id);
        do $$ begin
          if exists(select 1 from pg_roles where rolname='duora_app') then
            revoke update,delete,truncate on safety.photo_review_appeal_evidence_accesses from duora_app;
          end if;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve immutable appeal access and audit history; use a reviewed compatible forward change.");
}
