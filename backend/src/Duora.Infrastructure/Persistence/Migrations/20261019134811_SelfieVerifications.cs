using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SelfieVerifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stop the retired consumer before this migration. Fence any outstanding leases.
            migrationBuilder.Sql("""
                update ops.outbox_deliveries
                set status = 'Expired', completed_at = now(), lease_expires_at = null,
                    fencing_token = fencing_token + 1, last_error = 'AgeVerificationRetired'
                where consumer = 'age-verification-provider' and status in ('Pending', 'InFlight');
                """);

            migrationBuilder.CreateTable(
                name: "selfie_verifications",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    feedback_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_selfie_verifications", x => x.id);
                    table.CheckConstraint("ck_selfie_state", "state IN ('Pending','Capturing','Processing','Passed','RecaptureRequired','Inconclusive','Expired')");
                    table.CheckConstraint("ck_selfie_version", "version >= 0");
                    table.ForeignKey(
                        name: "fk_selfie_verifications_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_selfie_provider_reference",
                schema: "identity",
                table: "selfie_verifications",
                column: "provider_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_selfie_open_user",
                schema: "identity",
                table: "selfie_verifications",
                column: "user_id",
                unique: true,
                filter: "state IN ('Pending','Capturing','Processing')");

            migrationBuilder.CreateIndex(
                name: "ix_selfie_user_requested_at",
                schema: "identity",
                table: "selfie_verifications",
                columns: new[] { "user_id", "requested_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "selfie_verifications",
                schema: "identity");
        }
    }
}
