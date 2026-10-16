using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminMfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "last_totp_step",
                schema: "identity",
                table: "users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "totp_verified_at",
                schema: "identity",
                table: "login_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "mfa_verified_at",
                schema: "identity",
                table: "login_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "policy",
                schema: "identity",
                table: "login_sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Normal");

            // As sessões existentes são de participante; daqui em diante o login sempre informa a política, e
            // sem default um INSERT que a esqueça falha em vez de abrir sessão normal para conta privilegiada.
            migrationBuilder.Sql("alter table identity.login_sessions alter column policy drop default;");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reauthenticated_at",
                schema: "identity",
                table: "login_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.InsertData(
                schema: "identity",
                table: "roles",
                columns: new[] { "id", "concurrency_stamp", "name", "normalized_name" },
                values: new object[,]
                {
                    { new Guid("01920000-0000-7000-8000-000000000001"), "01920000-0000-7000-8000-000000000001", "participant", "PARTICIPANT" },
                    { new Guid("01920000-0000-7000-8000-000000000002"), "01920000-0000-7000-8000-000000000002", "moderator", "MODERATOR" },
                    { new Guid("01920000-0000-7000-8000-000000000003"), "01920000-0000-7000-8000-000000000003", "product_admin", "PRODUCT_ADMIN" },
                    { new Guid("01920000-0000-7000-8000-000000000004"), "01920000-0000-7000-8000-000000000004", "operator", "OPERATOR" }
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_login_sessions_policy",
                schema: "identity",
                table: "login_sessions",
                sql: "policy IN ('Normal', 'Administrative')");

            // D13: conta privilegiada não é participante. A regra fica no banco para valer em qualquer caminho de
            // concessão (Identity, comando operacional, SQL de suporte). O lock na linha da conta serializa
            // concessões simultâneas; sem ele, duas transações veriam a conta sem papel e gravariam (write skew).
            // A releitura depois do lock enxerga a concessão da outra transação porque, em READ COMMITTED (o
            // isolamento da aplicação), cada comando da função tira um snapshot novo. Em REPEATABLE READ ou
            // SERIALIZABLE a segunda transação falharia com erro de serialização em vez disso: também seguro, mas
            // quem mudar o isolamento precisa tratar o retry.
            migrationBuilder.Sql(
                """
                create function identity.enforce_privileged_is_not_participant() returns trigger
                language plpgsql as $$
                declare
                    granting_participant boolean;
                begin
                    perform 1 from identity.users where id = new.user_id for no key update;
                    select r.normalized_name = 'PARTICIPANT' into granting_participant
                    from identity.roles r where r.id = new.role_id;
                    if exists (
                        select 1
                        from identity.user_roles ur
                        join identity.roles r on r.id = ur.role_id
                        where ur.user_id = new.user_id
                          and not (tg_op = 'UPDATE' and ur.user_id = old.user_id and ur.role_id = old.role_id)
                          and (r.normalized_name = 'PARTICIPANT') is distinct from granting_participant)
                    then
                        raise exception 'Conta privilegiada não pode ter o papel de participante.'
                            using errcode = 'check_violation',
                                  constraint = 'ck_user_roles_privileged_is_not_participant';
                    end if;
                    return new;
                end;
                $$;

                create trigger trg_user_roles_privileged_is_not_participant
                    before insert or update on identity.user_roles
                    for each row execute function identity.enforce_privileged_is_not_participant();
                """);

            // I1: até aqui o cadastro não gravava papel. Toda conta sem papel é de participante; daqui em diante o
            // cadastro grava o papel na mesma transação, e o trigger acima passa a valer para elas também.
            migrationBuilder.Sql(
                """
                insert into identity.user_roles (user_id, role_id)
                select u.id, '01920000-0000-7000-8000-000000000001'::uuid
                from identity.users u
                where not exists (select 1 from identity.user_roles ur where ur.user_id = u.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop trigger trg_user_roles_privileged_is_not_participant on identity.user_roles;
                drop function identity.enforce_privileged_is_not_participant();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_login_sessions_policy",
                schema: "identity",
                table: "login_sessions");

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "roles",
                keyColumn: "id",
                keyValue: new Guid("01920000-0000-7000-8000-000000000001"));

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "roles",
                keyColumn: "id",
                keyValue: new Guid("01920000-0000-7000-8000-000000000002"));

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "roles",
                keyColumn: "id",
                keyValue: new Guid("01920000-0000-7000-8000-000000000003"));

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "roles",
                keyColumn: "id",
                keyValue: new Guid("01920000-0000-7000-8000-000000000004"));

            migrationBuilder.DropColumn(
                name: "last_totp_step",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "totp_verified_at",
                schema: "identity",
                table: "login_sessions");

            migrationBuilder.DropColumn(
                name: "mfa_verified_at",
                schema: "identity",
                table: "login_sessions");

            migrationBuilder.DropColumn(
                name: "policy",
                schema: "identity",
                table: "login_sessions");

            migrationBuilder.DropColumn(
                name: "reauthenticated_at",
                schema: "identity",
                table: "login_sessions");
        }
    }
}
