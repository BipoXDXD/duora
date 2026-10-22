using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Empty initial decision, manifest and command histories; never reclassifies existing uploads.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261022194500_PhotoReviewDecisions")]
public sealed class PhotoReviewDecisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table safety.photo_review_decisions (
          id uuid primary key check (id <> '00000000-0000-0000-0000-000000000000'),
          case_id uuid not null references safety.photo_review_cases(id) on delete restrict,
          case_version bigint not null check (case_version >= 3),
          actor_id uuid not null references identity.users(id) on delete restrict,
          decision varchar(8) not null check (decision='reject'),
          reason_code varchar(32) not null check (reason_code='does_not_meet_policy'),
          candidate_generation uuid not null references media.review_candidates(generation) on delete restrict,
          policy_version varchar(64) not null check (policy_version ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
          decided_at timestamptz not null check (isfinite(decided_at)),
          candidate_retention_until_snapshot timestamptz not null,
          history_retain_until timestamptz not null,
          check (isfinite(candidate_retention_until_snapshot)
            and candidate_retention_until_snapshot=decided_at+interval '168 hours'),
          check (isfinite(history_retain_until) and history_retain_until >=
            (((decided_at at time zone 'UTC')+interval '1 year') at time zone 'UTC')),
          unique(case_id,case_version));
        create table safety.photo_review_decision_variants (
          decision_id uuid not null references safety.photo_review_decisions(id) on delete restrict,
          variant varchar(8) not null check (variant in ('thumb','card','full')),
          size_bytes bigint not null check (size_bytes between 1 and 16777216),
          sha256 bytea not null check (octet_length(sha256)=32),
          primary key(decision_id,variant));
        create table safety.photo_review_decision_commands (
          actor_id uuid not null references identity.users(id) on delete restrict,
          operation varchar(8) not null check (operation='reject'),
          key_hash bytea not null check (octet_length(key_hash)=32),
          case_id uuid not null references safety.photo_review_cases(id) on delete restrict,
          intent_sha256 bytea not null check (octet_length(intent_sha256)=32),
          result_version bigint not null check (result_version >= 3),
          result_photo_version bigint not null check (result_photo_version >= 2),
          created_at timestamptz not null check (isfinite(created_at)),
          primary key(actor_id,operation,key_hash));
        do $$ begin
          if exists(select 1 from pg_roles where rolname='duora_app') then
            revoke update,delete,truncate on safety.photo_review_decisions,safety.photo_review_decision_variants,
              safety.photo_review_decision_commands from duora_app;
          end if;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve human decisions, manifests and receipts; use a reviewed compatible forward change.");
}
