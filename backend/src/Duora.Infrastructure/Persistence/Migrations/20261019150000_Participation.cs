using Duora.Infrastructure.Participation;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DuoraDbContext))]
[Migration("20261019150000_Participation")]
public sealed class Participation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ParticipationSchema.Create);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Participation contains private agreements and pinned sessions. Pause matching and roll back the binary; retain the additive schema.");
}
