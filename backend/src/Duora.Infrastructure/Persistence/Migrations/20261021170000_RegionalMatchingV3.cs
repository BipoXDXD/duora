using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DuoraDbContext))]
[Migration("20261021170000_RegionalMatchingV3")]
public sealed class RegionalMatchingV3 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table events.rounds drop constraint rounds_algorithm_version_check;
        alter table events.rounds add constraint rounds_algorithm_version_check check(algorithm_version in (2,3));
        alter table events.pairings drop constraint pairings_algorithm_version_check;
        alter table events.pairings add constraint pairings_algorithm_version_check check(algorithm_version in (2,3));
        alter table events.rounds add constraint ux_rounds_algorithm unique(id,event_id,algorithm_version);
        alter table events.pairings add constraint fk_pairings_round_algorithm
          foreign key(round_id,event_id,algorithm_version) references events.rounds(id,event_id,algorithm_version);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Pause matching before binary rollback; preserve V3 history and regional choices. Use a reviewed forward migration.");
}
