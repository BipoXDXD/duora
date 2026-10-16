using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgeVerifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "age_verifications",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    policy_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    request_nonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_flow_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_age_verifications", x => x.id);
                    table.CheckConstraint("ck_age_verifications_decision_source", "decision_source IS NULL OR decision_source IN ('Callback', 'ProviderQuery', 'ManualReview')");
                    table.CheckConstraint("ck_age_verifications_flow_url", "provider_flow_url IS NULL OR provider_flow_url LIKE 'https://%'");
                    table.CheckConstraint("ck_age_verifications_state", "state IN ('Pending', 'VerifiedAdult', 'Rejected', 'Expired', 'ManualReview')");
                    table.CheckConstraint("ck_age_verifications_verified_expiry", "state <> 'VerifiedAdult' OR expires_at IS NOT NULL");
                    table.CheckConstraint("ck_age_verifications_version", "version >= 0");
                    table.ForeignKey(
                        name: "fk_age_verifications_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_age_verifications_user_id_requested_at",
                schema: "identity",
                table: "age_verifications",
                columns: new[] { "user_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_age_verifications_open_user",
                schema: "identity",
                table: "age_verifications",
                column: "user_id",
                unique: true,
                filter: "state IN ('Pending', 'ManualReview')");

            migrationBuilder.CreateIndex(
                name: "ux_age_verifications_provider_provider_reference",
                schema: "identity",
                table: "age_verifications",
                columns: new[] { "provider", "provider_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_age_verifications_request_nonce",
                schema: "identity",
                table: "age_verifications",
                column: "request_nonce",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "age_verifications",
                schema: "identity");
        }
    }
}
