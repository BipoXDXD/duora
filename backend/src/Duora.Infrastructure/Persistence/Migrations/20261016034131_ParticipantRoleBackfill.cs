using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Duora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ParticipantRoleBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Conta sem papel é participante (D13). A migration AdminMfa já fez o backfill, mas uma instância web
            // ainda na versão anterior pode ter cadastrado contas sem papel durante o deploy; repetir é inofensivo.
            migrationBuilder.Sql(
                """
                insert into identity.user_roles (user_id, role_id)
                select u.id, r.id
                from identity.users u
                join identity.roles r on r.name = 'participant'
                where not exists (select 1 from identity.user_roles ur where ur.user_id = u.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sem volta: não há como distinguir o papel gravado aqui do gravado pelo cadastro, e tirar o papel de
            // participante de uma conta não a torna mais segura.
        }
    }
}
