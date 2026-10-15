using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SafetyBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "safety");

            migrationBuilder.CreateTable(
                name: "blocks",
                schema: "safety",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocks", x => x.id);
                    table.CheckConstraint("ck_blocks_not_self", "blocker_id <> blocked_id");
                    table.ForeignKey(
                        name: "fk_blocks_blocked",
                        column: x => x.blocked_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_blocks_blocker",
                        column: x => x.blocker_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_guards",
                schema: "safety",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_guards", x => x.user_id);
                    table.CheckConstraint("ck_user_guards_security_version", "security_version >= 0");
                    table.ForeignKey(
                        name: "fk_user_guards_users",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_blocks_blocked_id",
                schema: "safety",
                table: "blocks",
                column: "blocked_id");

            migrationBuilder.CreateIndex(
                name: "ux_blocks_pair",
                schema: "safety",
                table: "blocks",
                columns: new[] { "blocker_id", "blocked_id" },
                unique: true);

            // G7: toda conta que já existe ganha a guarda agora; as novas a recebem sob demanda, no primeiro
            // bloqueio, sem o cadastro do Identity precisar conhecer o Safety. Idempotente.
            migrationBuilder.Sql(
                """
                INSERT INTO safety.user_guards (user_id)
                SELECT id FROM identity.users
                ON CONFLICT (user_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocks",
                schema: "safety");

            migrationBuilder.DropTable(
                name: "user_guards",
                schema: "safety");
        }
    }
}
