using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DuoraDbContext))]
[Migration("20261021200000_CountryDatabaseUpdates")]
public sealed class CountryDatabaseUpdates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table ops.jobs drop constraint jobs_kind_check;
        alter table ops.jobs add constraint jobs_kind_check check(kind in
          ('build-round','close-round','expire-invite','game-deadline','retention','country-database-update'));
        create unique index ux_jobs_country_database on ops.jobs(kind) where kind='country-database-update';
        create table ops.country_database_versions (
          sha256 text primary key check(sha256 ~ '^[0-9a-f]{64}$'),
          manifest jsonb not null check(octet_length(manifest::text)<=8192 and manifest->>'Sha256'=sha256));
        revoke update,delete,truncate on ops.country_database_versions from duora_app;
        create table ops.country_database_state (
          singleton boolean primary key default true check(singleton),
          active_sha256 text references ops.country_database_versions(sha256),
          active_fencing_token bigint not null default 0 check(active_fencing_token>=0),
          first_failure_at timestamptz, last_attempt_at timestamptz,
          last_error_code text check(length(last_error_code)<=64),
          blocked boolean not null default false);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Keep the additive schema and manifests. Disable the updater before binary rollback; use a reviewed forward migration.");
}
