using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Only new processing commits enqueue cleanup; never scan or delete existing media on upgrade.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261022143000_OriginalCleanup")]
public sealed class OriginalCleanup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table media.original_cleanup (
          upload_id uuid constraint pk_original_cleanup primary key,
          object_key varchar(128) not null,
          created_at timestamptz not null,
          available_at timestamptz not null,
          attempts integer not null default 0,
          fencing_token uuid null,
          lease_until timestamptz null,
          constraint fk_original_cleanup_upload foreign key (upload_id) references media.uploads(id) on delete restrict,
          constraint ux_original_cleanup_key unique (object_key),
          constraint ck_original_cleanup_key check (object_key ~ '^quarantine/[0-9a-f]{32}$'),
          constraint ck_original_cleanup_attempts check (attempts >= 0),
          constraint ck_original_cleanup_dates check (isfinite(created_at) and isfinite(available_at)
            and (lease_until is null or isfinite(lease_until))),
          constraint ck_original_cleanup_lease check ((lease_until is null) = (fencing_token is null)));
        create index ix_original_cleanup_available on media.original_cleanup (available_at, upload_id);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve outstanding cleanup obligations; use a compatible release or a reviewed forward migration.");
}
