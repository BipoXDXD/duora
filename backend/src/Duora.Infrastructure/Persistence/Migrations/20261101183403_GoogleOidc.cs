using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GoogleOidc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "authentication_method",
                schema: "identity",
                table: "login_sessions",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "LocalPassword");

            migrationBuilder.CreateTable(
                name: "external_auth_flows",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    browser_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    stage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    protected_proof = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expected_stamp = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_auth_flows", x => x.id);
                    table.CheckConstraint("ck_external_auth_flows_binding", "octet_length(browser_hash)=32 AND attempts BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_external_auth_flows_purpose", "purpose IN ('Login','Register','Link')");
                    table.CheckConstraint("ck_external_auth_flows_stage", "stage IN ('Created','Challenged','Validated','Registration','Complete','Failed')");
                    table.CheckConstraint("ck_external_auth_flows_window", "expires_at > created_at AND expires_at <= created_at + interval '5 minutes'");
                    table.ForeignKey(
                        name: "fk_external_auth_flows_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_user_logins_user_id_login_provider",
                schema: "identity",
                table: "user_logins",
                columns: new[] { "user_id", "login_provider" },
                unique: true,
                filter: "login_provider = 'google-oidc'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_login_sessions_authentication_method",
                schema: "identity",
                table: "login_sessions",
                sql: "authentication_method IN ('LocalPassword','GoogleOidc')");

            migrationBuilder.CreateIndex(
                name: "ix_external_auth_flows_expires_at",
                schema: "identity",
                table: "external_auth_flows",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_external_auth_flows_user_id",
                schema: "identity",
                table: "external_auth_flows",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_auth_flows",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "ux_user_logins_user_id_login_provider",
                schema: "identity",
                table: "user_logins");

            migrationBuilder.DropCheckConstraint(
                name: "ck_login_sessions_authentication_method",
                schema: "identity",
                table: "login_sessions");

            migrationBuilder.DropColumn(
                name: "authentication_method",
                schema: "identity",
                table: "login_sessions");
        }
    }
}
