using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>
/// Issue #300: reprovações da selfie em <c>identity.selfie_failures</c>, uma linha por tentativa reprovada. A chave
/// primária é a tentativa, então um resultado reentregue não conta duas vezes; a contagem é derivada das linhas, sem
/// contador a atualizar. Sem FK para <c>selfie_verifications</c>: a tentativa não aprovada sai em 30 dias (ADR-0029)
/// e a reprovação precisa continuar valendo no segundo ciclo. Sai com a conta (cascata), na aprovação e na revogação
/// da suspensão por selfie. Aditiva: tabela vazia, sem backfill; tentativas anteriores não contam.
/// </summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261030120000_SelfieFailures")]
public sealed class SelfieFailures : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table identity.selfie_failures (
          verification_id uuid constraint pk_selfie_failures primary key,
          user_id uuid not null,
          failed_at timestamptz not null,
          constraint fk_selfie_failures_users foreign key (user_id)
            references identity.users (id) on delete cascade,
          constraint ck_selfie_failures_failed_at check (isfinite(failed_at)));

        -- Leitura por conta em ordem de tempo, e índice da FK (o PostgreSQL não o cria sozinho).
        create index ix_selfie_failures_user on identity.selfie_failures (user_id, failed_at);
        """);

    // A release anterior ignora a tabela; apagá-la devolveria tentativas a quem já as esgotou.
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Keep identity.selfie_failures; roll back to a compatible release or a reviewed forward migration.");
}
