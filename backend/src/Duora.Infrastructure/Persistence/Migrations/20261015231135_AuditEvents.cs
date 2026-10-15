using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "ops",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_version = table.Column<long>(type: "bigint", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.CheckConstraint("ck_audit_events_action", "action ~ '^[a-z]+(\\.[a-z_]+)+$'");
                    table.CheckConstraint("ck_audit_events_actor_id", "actor_kind = 'System' OR actor_id IS NOT NULL");
                    table.CheckConstraint("ck_audit_events_actor_kind", "actor_kind IN ('User', 'Moderator', 'Admin', 'Operator', 'System')");
                    table.CheckConstraint("ck_audit_events_target_type", "target_type ~ '^[a-z][a-z_]{0,31}$'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor",
                schema: "ops",
                table: "audit_events",
                columns: new[] { "actor_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_target",
                schema: "ops",
                table: "audit_events",
                columns: new[] { "target_type", "target_id", "occurred_at" });

            // Append-only por GRANT (D3, TM-05; convenção de model.md, "Permissões de banco"): os default privileges
            // deram DML completo ao app; aqui ele fica só com INSERT e SELECT. O papel existe em toda instalação da
            // imagem do Duora (initdb); o banco de desenvolvimento e o dos testes comuns não o têm, e lá não há o
            // que revogar. AuditPrivilegesTests aplica esta migration com os papéis de produção.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'duora_app') THEN
                    REVOKE UPDATE, DELETE, TRUNCATE ON TABLE ops.audit_events FROM duora_app;
                  END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "ops");
        }
    }
}
