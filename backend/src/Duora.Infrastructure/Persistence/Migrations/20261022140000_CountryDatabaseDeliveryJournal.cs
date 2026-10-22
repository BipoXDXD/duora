using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DuoraDbContext))]
[Migration("20261022140000_CountryDatabaseDeliveryJournal")]
public sealed class CountryDatabaseDeliveryJournal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table ops.country_database_state add column backup_failed boolean not null default false;
        create table ops.country_database_deliveries (
          notification_id bigint primary key references ops.country_database_notifications(id) on delete cascade,
          attempted_at timestamptz not null, accepted_at timestamptz,
          constraint ck_country_database_delivery_time check(accepted_at is null or accepted_at>=attempted_at));
        revoke delete,truncate on ops.country_database_deliveries,ops.country_database_notifications from duora_app;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve delivery evidence. Disable the notifier and use a reviewed forward migration.");
}
