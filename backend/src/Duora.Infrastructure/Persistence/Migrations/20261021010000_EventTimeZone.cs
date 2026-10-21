using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DuoraDbContext))]
[Migration("20261021010000_EventTimeZone")]
public sealed class EventTimeZone : Migration
{
    // Historical instants do not reveal the original event zone. Preserve that uncertainty.
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "alter table events.events add column time_zone text null;");

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Roll back the binary and retain the additive time_zone column to preserve event zones.");
}
