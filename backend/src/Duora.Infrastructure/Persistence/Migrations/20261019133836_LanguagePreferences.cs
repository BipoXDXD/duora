using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LanguagePreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "locale_version",
                schema: "identity",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "preferred_locale",
                schema: "identity",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "preferences",
                schema: "profiles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allow_different_display_locales = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_preferences", x => x.user_id);
                    table.CheckConstraint("ck_preferences_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_preferences_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "communication_languages",
                schema: "profiles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language_code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_communication_languages", x => new { x.user_id, x.language_code });
                    table.CheckConstraint("ck_communication_languages_code", "language_code IN ('en', 'pt')");
                    table.ForeignKey(
                        name: "fk_communication_languages_preferences",
                        column: x => x.user_id,
                        principalSchema: "profiles",
                        principalTable: "preferences",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_locale_version",
                schema: "identity",
                table: "users",
                sql: "locale_version >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_preferred_locale",
                schema: "identity",
                table: "users",
                sql: "preferred_locale IN ('en-US', 'pt-BR')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "communication_languages",
                schema: "profiles");

            migrationBuilder.DropTable(
                name: "preferences",
                schema: "profiles");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_locale_version",
                schema: "identity",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_preferred_locale",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "locale_version",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "preferred_locale",
                schema: "identity",
                table: "users");
        }
    }
}
