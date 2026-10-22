using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Explicit grants start empty. History is immutable; no account receives permission by role or seed.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261022162000_PhotoReviewPermissions")]
public sealed class PhotoReviewPermissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table safety.photo_review_grants (
          id uuid primary key check (id <> '00000000-0000-0000-0000-000000000000'),
          user_id uuid not null references identity.users(id) on delete restrict,
          capability varchar(16) not null check (capability in ('assign','evidence','decide','appeal','preserve')),
          version bigint not null check (version >= 1),
          enabled boolean not null,
          occurred_at timestamptz not null check (isfinite(occurred_at)),
          unique(user_id,capability,version));
        do $$ begin
          if exists(select 1 from pg_roles where rolname='duora_app') then
            revoke update,delete,truncate on safety.photo_review_grants from duora_app;
          end if;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve review permission history and audits; use a reviewed compatible forward change.");
}
