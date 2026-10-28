using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>
/// Issue #93, decisão do dono em 28/10/2026: o resultado aprovado da selfie fica enquanto a conta existir. A exclusão
/// física da conta leva as tentativas junto (ON DELETE CASCADE), em vez de bloqueá-la. Só troca a ação da FK.
/// </summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261028151040_SelfieResultFollowsAccount")]
public sealed class SelfieResultFollowsAccount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table identity.selfie_verifications drop constraint fk_selfie_verifications_users;
        alter table identity.selfie_verifications add constraint fk_selfie_verifications_users
          foreign key (user_id) references identity.users (id) on delete cascade;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table identity.selfie_verifications drop constraint fk_selfie_verifications_users;
        alter table identity.selfie_verifications add constraint fk_selfie_verifications_users
          foreign key (user_id) references identity.users (id) on delete restrict;
        """);
}
