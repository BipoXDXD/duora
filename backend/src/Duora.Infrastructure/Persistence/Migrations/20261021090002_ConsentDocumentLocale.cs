using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConsentDocumentLocale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "document_locale",
                schema: "identity",
                table: "consent_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_consent_records_document_locale",
                schema: "identity",
                table: "consent_records",
                sql: "document_locale IN ('pt-BR', 'en-US')");

            // Evidence is immutable, including unknown historical locales. DELETE remains available for the
            // existing pending-registration replacement and account-deletion/retention flows (ADR-0019).
            migrationBuilder.Sql("""
                CREATE FUNCTION identity.prevent_consent_record_update() RETURNS trigger
                LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW IS DISTINCT FROM OLD THEN
                        RAISE EXCEPTION 'Consent evidence cannot be updated'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_consent_records_immutable';
                    END IF;
                    RETURN NEW;
                END;
                $function$;
                CREATE TRIGGER consent_records_immutable
                    BEFORE UPDATE ON identity.consent_records
                    FOR EACH ROW EXECUTE FUNCTION identity.prevent_consent_record_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER consent_records_immutable ON identity.consent_records;
                DROP FUNCTION identity.prevent_consent_record_update();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_consent_records_document_locale",
                schema: "identity",
                table: "consent_records");

            migrationBuilder.DropColumn(
                name: "document_locale",
                schema: "identity",
                table: "consent_records");
        }
    }
}
