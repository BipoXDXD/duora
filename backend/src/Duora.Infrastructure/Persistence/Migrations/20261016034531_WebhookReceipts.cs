using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WebhookReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_receipts",
                schema: "ops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    signature_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payload_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_receipts", x => x.id);
                    table.CheckConstraint("ck_webhook_receipts_payload_sha256", "octet_length(payload_sha256) = 32");
                    table.CheckConstraint("ck_webhook_receipts_payload_size", "octet_length(payload::text) <= 65536");
                    table.CheckConstraint("ck_webhook_receipts_state", "state IN ('Received', 'Processed', 'Rejected')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_receipts_received_at",
                schema: "ops",
                table: "webhook_receipts",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ux_webhook_receipts_provider_provider_event_id",
                schema: "ops",
                table: "webhook_receipts",
                columns: new[] { "provider", "provider_event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_receipts",
                schema: "ops");
        }
    }
}
