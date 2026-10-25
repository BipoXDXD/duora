using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Versioned appeal routing history and command receipts; no reviewer is enabled by migration.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261025040000_PhotoReviewAppealAssignments")]
public sealed class PhotoReviewAppealAssignments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table safety.photo_review_appeals drop constraint photo_review_appeals_version_check;
        alter table safety.photo_review_appeals add constraint photo_review_appeals_version_check check (version>=1);
        create table safety.photo_review_appeal_assignments (
          id uuid primary key check (id <> '00000000-0000-0000-0000-000000000000'),
          appeal_id uuid not null references safety.photo_review_appeals(id) on delete restrict,
          appeal_version bigint not null check (appeal_version>=2),
          reviewer_id uuid not null references identity.users(id) on delete restrict,
          assigned_by uuid not null references identity.users(id) on delete restrict,
          assigned_at timestamptz not null check (isfinite(assigned_at)),
          unique(appeal_id,appeal_version));
        create table safety.photo_review_appeal_assignment_commands (
          actor_id uuid not null references identity.users(id) on delete restrict,
          key_hash bytea not null check (octet_length(key_hash)=32),
          intent_sha256 bytea not null check (octet_length(intent_sha256)=32),
          appeal_id uuid not null references safety.photo_review_appeals(id) on delete restrict,
          result_version bigint not null check (result_version>=2),
          created_at timestamptz not null check (isfinite(created_at)),
          foreign key(appeal_id,result_version)
            references safety.photo_review_appeal_assignments(appeal_id,appeal_version) on delete restrict,
          primary key(actor_id,key_hash));
        do $$ begin
          if exists(select 1 from pg_roles where rolname='duora_app') then
            revoke update,delete,truncate on safety.photo_review_appeal_assignments from duora_app;
            revoke update,delete,truncate on safety.photo_review_appeal_assignment_commands from duora_app;
          end if;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve appeal assignment history and receipts; use a reviewed compatible forward change.");
}
