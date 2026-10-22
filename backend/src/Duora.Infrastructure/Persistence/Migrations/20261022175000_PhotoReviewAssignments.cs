using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>New empty assignment/receipt histories; no legacy file, photo or permission is modified.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261022175000_PhotoReviewAssignments")]
public sealed class PhotoReviewAssignments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table safety.photo_review_assignments (
          id uuid primary key check (id <> '00000000-0000-0000-0000-000000000000'),
          case_id uuid not null references safety.photo_review_cases(id) on delete restrict,
          reviewer_id uuid not null references identity.users(id) on delete restrict,
          assigned_by uuid not null references identity.users(id) on delete restrict,
          assigned_at timestamptz not null check (isfinite(assigned_at)),
          case_version bigint not null check (case_version >= 2),
          unique(case_id,case_version));
        create table safety.photo_review_commands (
          actor_id uuid not null references identity.users(id) on delete restrict,
          operation varchar(16) not null check (operation='assign'),
          key_hash bytea not null check (octet_length(key_hash)=32),
          case_id uuid not null references safety.photo_review_cases(id) on delete restrict,
          intent_sha256 bytea not null check (octet_length(intent_sha256)=32),
          result_version bigint not null check (result_version >= 2),
          created_at timestamptz not null check (isfinite(created_at)),
          primary key(actor_id,operation,key_hash));
        do $$ begin
          if exists(select 1 from pg_roles where rolname='duora_app') then
            revoke update,delete,truncate on safety.photo_review_assignments,safety.photo_review_commands from duora_app;
          end if;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve assignments, command receipts and audit history; use a reviewed compatible forward change.");
}
