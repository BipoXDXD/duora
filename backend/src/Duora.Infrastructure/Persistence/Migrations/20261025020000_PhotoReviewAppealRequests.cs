using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>One owner appeal root and append-only request receipts; existing decisions remain initial facts.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261025020000_PhotoReviewAppealRequests")]
public sealed class PhotoReviewAppealRequests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table safety.photo_review_appeals (
          id uuid primary key check (id <> '00000000-0000-0000-0000-000000000000'),
          case_id uuid not null unique references safety.photo_review_cases(id) on delete restrict,
          original_decision_id uuid not null unique references safety.photo_review_decisions(id) on delete restrict,
          owner_id uuid not null references identity.users(id) on delete restrict,
          candidate_generation uuid not null references media.review_candidates(generation) on delete restrict,
          policy_version varchar(64) not null check (policy_version ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
          status varchar(24) not null check (status='Open'),
          version bigint not null check (version=1),
          original_rejected_at timestamptz not null check (isfinite(original_rejected_at)),
          requested_at timestamptz not null check (isfinite(requested_at)),
          response_target_at timestamptz not null check (isfinite(response_target_at)),
          maximum_retention_until timestamptz not null check (isfinite(maximum_retention_until)),
          closed_at timestamptz null check (closed_at is null),
          check (requested_at >= original_rejected_at and requested_at < original_rejected_at+interval '168 hours'),
          check (response_target_at=requested_at+interval '168 hours'),
          check (maximum_retention_until=original_rejected_at+interval '720 hours'));
        alter table safety.photo_review_decisions
          add column appeal_id uuid null references safety.photo_review_appeals(id) on delete restrict;
        create table safety.photo_review_appeal_commands (
          actor_id uuid not null references identity.users(id) on delete restrict,
          key_hash bytea not null check (octet_length(key_hash)=32),
          intent_sha256 bytea not null check (octet_length(intent_sha256)=32),
          appeal_id uuid not null references safety.photo_review_appeals(id) on delete restrict,
          photo_id uuid not null references media.uploads(id) on delete restrict,
          result_photo_version bigint not null check (result_photo_version >= 2),
          result_requested_at timestamptz not null check (isfinite(result_requested_at)),
          result_response_target_at timestamptz not null check (isfinite(result_response_target_at)),
          result_maximum_retention_until timestamptz not null check (isfinite(result_maximum_retention_until)),
          created_at timestamptz not null check (isfinite(created_at)),
          check (created_at=result_requested_at and result_response_target_at=result_requested_at+interval '168 hours'
            and result_maximum_retention_until > result_requested_at),
          primary key(actor_id,key_hash));
        do $$ begin
          if exists(select 1 from pg_roles where rolname='duora_app') then
            revoke delete,truncate on safety.photo_review_appeals from duora_app;
            revoke update,delete,truncate on safety.photo_review_appeal_commands from duora_app;
          end if;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve appeal roots, original decisions and request receipts; use a reviewed compatible forward change.");
}
