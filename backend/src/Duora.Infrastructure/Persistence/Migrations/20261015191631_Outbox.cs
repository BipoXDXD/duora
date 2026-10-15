using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Outbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ops");

            migrationBuilder.CreateTable(
                name: "outbox_events",
                schema: "ops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_version = table.Column<long>(type: "bigint", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_deliveries",
                schema: "ops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbox_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    recipient_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    last_error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_deliveries", x => x.id);
                    table.CheckConstraint("ck_outbox_deliveries_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_outbox_deliveries_lease", "(status = 'InFlight') = (lease_expires_at IS NOT NULL)");
                    table.CheckConstraint("ck_outbox_deliveries_status", "status IN ('Pending', 'InFlight', 'Delivered', 'Failed', 'Expired')");
                    table.ForeignKey(
                        name: "fk_outbox_deliveries_outbox_events",
                        column: x => x.outbox_event_id,
                        principalSchema: "ops",
                        principalTable: "outbox_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_deliveries_in_flight",
                schema: "ops",
                table: "outbox_deliveries",
                columns: new[] { "consumer", "lease_expires_at" },
                filter: "status = 'InFlight'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_deliveries_pending",
                schema: "ops",
                table: "outbox_deliveries",
                columns: new[] { "consumer", "available_at", "id" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_outbox_deliveries_effect",
                schema: "ops",
                table: "outbox_deliveries",
                columns: new[] { "outbox_event_id", "consumer", "recipient_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_deliveries",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "outbox_events",
                schema: "ops");
        }
    }
}
