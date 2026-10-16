using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminBootstrap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_actor_id",
                schema: "ops",
                table: "audit_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_actor_kind",
                schema: "ops",
                table: "audit_events");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "temporary_password_expires_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor_label",
                schema: "ops",
                table: "audit_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_temporary_password_requires_confirmed_email",
                schema: "identity",
                table: "users",
                sql: "temporary_password_expires_at IS NULL OR email_confirmed");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_actor_id",
                schema: "ops",
                table: "audit_events",
                sql: "actor_kind IN ('System', 'TerminalOperator') OR actor_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_actor_kind",
                schema: "ops",
                table: "audit_events",
                sql: "actor_kind IN ('User', 'Moderator', 'Admin', 'Operator', 'TerminalOperator', 'System')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_actor_label",
                schema: "ops",
                table: "audit_events",
                sql: "CASE WHEN actor_kind = 'TerminalOperator' THEN actor_id IS NULL AND actor_label IS NOT NULL AND actor_label ~ '^[a-z0-9_][a-z0-9._-]{0,31}$' ELSE actor_label IS NULL END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_temporary_password_requires_confirmed_email",
                schema: "identity",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_actor_id",
                schema: "ops",
                table: "audit_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_actor_kind",
                schema: "ops",
                table: "audit_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_actor_label",
                schema: "ops",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "temporary_password_expires_at",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "actor_label",
                schema: "ops",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_actor_id",
                schema: "ops",
                table: "audit_events",
                sql: "actor_kind = 'System' OR actor_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_actor_kind",
                schema: "ops",
                table: "audit_events",
                sql: "actor_kind IN ('User', 'Moderator', 'Admin', 'Operator', 'System')");
        }
    }
}
