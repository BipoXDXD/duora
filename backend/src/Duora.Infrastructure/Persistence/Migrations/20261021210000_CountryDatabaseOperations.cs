using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DuoraDbContext))]
[Migration("20261021210000_CountryDatabaseOperations")]
public sealed class CountryDatabaseOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table ops.country_database_state
          add column last_observed_at timestamptz,
          add column review_required boolean not null default false,
          add column repair_authorized boolean not null default false;
        create table ops.country_database_approvals (
          sha256 text primary key references ops.country_database_versions(sha256),
          promoted_at timestamptz not null);
        insert into ops.country_database_approvals(sha256,promoted_at)
          select active_sha256,coalesce(last_attempt_at,now()) from ops.country_database_state where active_sha256 is not null;
        create table ops.country_database_retired (
          sha256 text primary key references ops.country_database_versions(sha256), retired_at timestamptz not null);
        revoke update,delete,truncate on ops.country_database_approvals,ops.country_database_retired from duora_app;
        create table ops.country_database_incidents (
          code text primary key check(code in ('Age','NoExecution','MissingBase','Unauthorized','Schema','Integrity',
            'ApprovalRequired','Exhausted','Clock','Validation','Disk','Backup')),
          severity integer not null check(severity in (1,2)),
          opened_at timestamptz not null, last_notified_at timestamptz not null);
        create table ops.country_database_notifications (
          id bigint generated always as identity primary key,
          code text not null check(code in ('Age','NoExecution','MissingBase','Unauthorized','Schema','Integrity',
            'ApprovalRequired','Exhausted','Clock','Validation','Disk','Backup','Promoted','Recovered','Retired','ReviewReleased')),
          severity integer not null check(severity in (0,1,2)), recovered boolean not null default false,
          occurred_at timestamptz not null, available_at timestamptz not null,
          delivered_at timestamptz, outbound boolean not null default true,
          sha256 text check(sha256 ~ '^[0-9a-f]{64}$'));
        create index ix_country_database_notifications_pending on ops.country_database_notifications(available_at,id)
          where outbound and delivered_at is null;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Keep additive operational evidence. Disable services and use a reviewed forward migration.");
}
