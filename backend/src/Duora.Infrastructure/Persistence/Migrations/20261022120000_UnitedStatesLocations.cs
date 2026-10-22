using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DuoraDbContext))]
[Migration("20261022120000_UnitedStatesLocations")]
public sealed class UnitedStatesLocations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.InsertData(schema: "profiles", table: "countries", column: "code", value: "US");
        migrationBuilder.InsertData(schema: "profiles", table: "subdivisions", columns: ["code", "country_code"],
            values: new object[,]
            {
                { "US-AK", "US" },
                { "US-AL", "US" },
                { "US-AR", "US" },
                { "US-AZ", "US" },
                { "US-CA", "US" },
                { "US-CO", "US" },
                { "US-CT", "US" },
                { "US-DC", "US" },
                { "US-DE", "US" },
                { "US-FL", "US" },
                { "US-GA", "US" },
                { "US-HI", "US" },
                { "US-IA", "US" },
                { "US-ID", "US" },
                { "US-IL", "US" },
                { "US-IN", "US" },
                { "US-KS", "US" },
                { "US-KY", "US" },
                { "US-LA", "US" },
                { "US-MA", "US" },
                { "US-MD", "US" },
                { "US-ME", "US" },
                { "US-MI", "US" },
                { "US-MN", "US" },
                { "US-MO", "US" },
                { "US-MS", "US" },
                { "US-MT", "US" },
                { "US-NC", "US" },
                { "US-ND", "US" },
                { "US-NE", "US" },
                { "US-NH", "US" },
                { "US-NJ", "US" },
                { "US-NM", "US" },
                { "US-NV", "US" },
                { "US-NY", "US" },
                { "US-OH", "US" },
                { "US-OK", "US" },
                { "US-OR", "US" },
                { "US-PA", "US" },
                { "US-RI", "US" },
                { "US-SC", "US" },
                { "US-SD", "US" },
                { "US-TN", "US" },
                { "US-TX", "US" },
                { "US-UT", "US" },
                { "US-VA", "US" },
                { "US-VT", "US" },
                { "US-WA", "US" },
                { "US-WI", "US" },
                { "US-WV", "US" },
                { "US-WY", "US" },
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve declared locations and preferences. Pause profile edits before binary rollback; use a reviewed forward migration.");
}
