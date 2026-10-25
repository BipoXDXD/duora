using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>Additive evidence only: no inferred fingerprint or locale for historical records.</summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261025120000_ConsentDocumentFingerprint")]
public sealed class ConsentDocumentFingerprint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "document_sha256", schema: "identity",
            table: "consent_records", type: "text", nullable: true);
        migrationBuilder.AddCheckConstraint(name: "ck_consent_records_document_sha256", schema: "identity",
            table: "consent_records", sql: "document_sha256 ~ '^[a-f0-9]{64}$'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A rollback must not silently destroy acceptance evidence already collected by the new writer.
        migrationBuilder.Sql("""
            LOCK TABLE identity.consent_records IN ACCESS EXCLUSIVE MODE;
            DO $guard$ BEGIN
                IF EXISTS (SELECT 1 FROM identity.consent_records WHERE document_sha256 IS NOT NULL) THEN
                    RAISE EXCEPTION 'Cannot remove recorded legal document fingerprints';
                END IF;
            END $guard$;
            """);
        migrationBuilder.DropCheckConstraint(name: "ck_consent_records_document_sha256", schema: "identity", table: "consent_records");
        migrationBuilder.DropColumn(name: "document_sha256", schema: "identity", table: "consent_records");
    }
}
