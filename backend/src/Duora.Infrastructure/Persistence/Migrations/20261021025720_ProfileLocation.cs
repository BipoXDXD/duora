using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProfileLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "country_code",
                schema: "profiles",
                table: "profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subdivision_code",
                schema: "profiles",
                table: "profiles",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "countries",
                schema: "profiles",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_countries", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "subdivisions",
                schema: "profiles",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subdivisions", x => x.code);
                    table.UniqueConstraint("ak_subdivisions_country_code", x => new { x.country_code, x.code });
                    table.ForeignKey(
                        name: "fk_subdivisions_countries",
                        column: x => x.country_code,
                        principalSchema: "profiles",
                        principalTable: "countries",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "profiles",
                table: "countries",
                column: "code",
                value: "BR");

            migrationBuilder.InsertData(
                schema: "profiles",
                table: "subdivisions",
                columns: new[] { "code", "country_code" },
                values: new object[,]
                {
                    { "BR-AC", "BR" },
                    { "BR-AL", "BR" },
                    { "BR-AM", "BR" },
                    { "BR-AP", "BR" },
                    { "BR-BA", "BR" },
                    { "BR-CE", "BR" },
                    { "BR-DF", "BR" },
                    { "BR-ES", "BR" },
                    { "BR-GO", "BR" },
                    { "BR-MA", "BR" },
                    { "BR-MG", "BR" },
                    { "BR-MS", "BR" },
                    { "BR-MT", "BR" },
                    { "BR-PA", "BR" },
                    { "BR-PB", "BR" },
                    { "BR-PE", "BR" },
                    { "BR-PI", "BR" },
                    { "BR-PR", "BR" },
                    { "BR-RJ", "BR" },
                    { "BR-RN", "BR" },
                    { "BR-RO", "BR" },
                    { "BR-RR", "BR" },
                    { "BR-RS", "BR" },
                    { "BR-SC", "BR" },
                    { "BR-SE", "BR" },
                    { "BR-SP", "BR" },
                    { "BR-TO", "BR" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_profiles_location",
                schema: "profiles",
                table: "profiles",
                columns: new[] { "country_code", "subdivision_code" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_profiles_location_country",
                schema: "profiles",
                table: "profiles",
                sql: "subdivision_code IS NULL OR country_code IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_profiles_countries",
                schema: "profiles",
                table: "profiles",
                column: "country_code",
                principalSchema: "profiles",
                principalTable: "countries",
                principalColumn: "code",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_profiles_subdivisions",
                schema: "profiles",
                table: "profiles",
                columns: new[] { "country_code", "subdivision_code" },
                principalSchema: "profiles",
                principalTable: "subdivisions",
                principalColumns: new[] { "country_code", "code" },
                onDelete: ReferentialAction.Restrict);
            // Backfill does not change version or updated_at: outstanding ETags remain valid.
            migrationBuilder.Sql("""
                -- Runtime cannot enable countries or alter the approved reference catalog.
                REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON profiles.countries, profiles.subdivisions FROM duora_app;

                UPDATE profiles.profiles
                SET country_code = 'BR', subdivision_code = region_code
                WHERE region_code IS NOT NULL;

                -- Bridge old binaries during expand. New binaries dual-write all three columns.
                CREATE FUNCTION profiles.sync_legacy_region() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.country_code IS NULL AND NEW.subdivision_code IS NULL AND NEW.region_code IS NOT NULL THEN
                            NEW.country_code := 'BR';
                            NEW.subdivision_code := NEW.region_code;
                        END IF;
                    ELSIF NEW.region_code IS DISTINCT FROM OLD.region_code
                        AND NEW.country_code IS NOT DISTINCT FROM OLD.country_code
                        AND NEW.subdivision_code IS NOT DISTINCT FROM OLD.subdivision_code THEN
                        NEW.country_code := CASE WHEN NEW.region_code IS NULL THEN NULL ELSE 'BR' END;
                        NEW.subdivision_code := NEW.region_code;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER sync_legacy_region BEFORE INSERT OR UPDATE ON profiles.profiles
                FOR EACH ROW EXECUTE FUNCTION profiles.sync_legacy_region();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
            "Retain the additive location schema and data. Roll back binaries with profile writes paused; never drop country-only locations.");
    }
}
