using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Media : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "media");

            migrationBuilder.CreateTable(
                name: "uploads",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    client_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quarantine_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    declared_content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content_sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    abandon_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rejection_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_uploads", x => x.id);
                    table.CheckConstraint("ck_uploads_client_key", "client_key ~ '^[A-Za-z0-9_-]{1,64}$'");
                    table.CheckConstraint("ck_uploads_content_sha256", "(content_sha256 IS NULL OR octet_length(content_sha256) = 32) AND (state IN ('Pending', 'Deleted') OR content_sha256 IS NOT NULL) AND (state <> 'Pending' OR content_sha256 IS NULL)");
                    table.CheckConstraint("ck_uploads_declared_content_type", "declared_content_type IN ('image/jpeg', 'image/png', 'image/webp')");
                    table.CheckConstraint("ck_uploads_rejection_code", "(rejection_code IS NULL OR rejection_code IN ('UNSUPPORTED_FORMAT', 'TYPE_MISMATCH', 'TOO_MANY_PIXELS', 'ANIMATED', 'CORRUPT_IMAGE', 'PROCESSING_LIMIT')) AND (state <> 'Rejected' OR rejection_code IS NOT NULL) AND (rejection_code IS NULL OR state IN ('Rejected', 'Deleted'))");
                    table.CheckConstraint("ck_uploads_size_bytes", "size_bytes BETWEEN 1 AND 5242880");
                    table.CheckConstraint("ck_uploads_state", "state IN ('Pending', 'Quarantined', 'Processing', 'Approved', 'Rejected', 'Deleted')");
                    table.CheckConstraint("ck_uploads_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_uploads_users_owner_id",
                        column: x => x.owner_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "media_assets",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    upload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    detected_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    original_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_assets", x => x.id);
                    table.CheckConstraint("ck_media_assets_detected_type", "detected_type IN ('image/jpeg', 'image/png', 'image/webp')");
                    table.CheckConstraint("ck_media_assets_dimensions", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_media_assets_pixels", "width::bigint * height <= 20000000");
                    table.CheckConstraint("ck_media_assets_sha256", "octet_length(sha256) = 32");
                    table.CheckConstraint("ck_media_assets_state", "state IN ('Approved', 'Rejected', 'Deleted')");
                    table.CheckConstraint("ck_media_assets_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_media_assets_uploads_upload_id",
                        column: x => x.upload_id,
                        principalSchema: "media",
                        principalTable: "uploads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_media_assets_users_owner_id",
                        column: x => x.owner_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "media_variants",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    object_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_variants", x => x.id);
                    table.CheckConstraint("ck_media_variants_dimensions", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_media_variants_kind", "kind IN ('thumb', 'card', 'full')");
                    table.CheckConstraint("ck_media_variants_size_bytes", "size_bytes > 0");
                    table.ForeignKey(
                        name: "fk_media_variants_media_assets_media_asset_id",
                        column: x => x.media_asset_id,
                        principalSchema: "media",
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_owner_id",
                schema: "media",
                table: "media_assets",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ux_media_assets_upload_id",
                schema: "media",
                table: "media_assets",
                column: "upload_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_media_variants_media_asset_id_kind",
                schema: "media",
                table: "media_variants",
                columns: new[] { "media_asset_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_media_variants_object_key",
                schema: "media",
                table: "media_variants",
                column: "object_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_uploads_abandon_after",
                schema: "media",
                table: "uploads",
                column: "abandon_after",
                filter: "state IN ('Pending', 'Quarantined')");

            migrationBuilder.CreateIndex(
                name: "ix_uploads_pending_owner",
                schema: "media",
                table: "uploads",
                column: "owner_id",
                filter: "state IN ('Pending', 'Quarantined', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "ux_uploads_owner_id_client_key",
                schema: "media",
                table: "uploads",
                columns: new[] { "owner_id", "client_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_uploads_quarantine_key",
                schema: "media",
                table: "uploads",
                column: "quarantine_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_variants",
                schema: "media");

            migrationBuilder.DropTable(
                name: "media_assets",
                schema: "media");

            migrationBuilder.DropTable(
                name: "uploads",
                schema: "media");
        }
    }
}
