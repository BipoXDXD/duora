using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>
/// Decisão do dono de 2026-10-29 (#173, ADR-0035): a espera da fila de revisão sobe de 72 h para 7 dias. Expand: a
/// constraint aceita os 7 dias e mantém os 72 h que a release anterior grava, para o rollback continuar escrevendo.
/// Nenhuma linha é reescrita: um caso pendente mantém o prazo e a retenção do candidato com que entrou na fila. O
/// contract (aceitar só 7 dias) fica para depois que nenhuma release em uso gravar 72 h e nenhum caso de 72 h estiver
/// pendente. Intervalo em horas, não em dias: em timestamptz, '7 days' segue o horário de verão do fuso da sessão, e o
/// código grava exatamente 168 h.
/// </summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261029120000_PhotoReviewSevenDayWait")]
public sealed class PhotoReviewSevenDayWait : Migration
{
    /// <summary>Falha rápido em vez de enfileirar o tráfego atrás do ACCESS EXCLUSIVE do ALTER; o migrator pode repetir.</summary>
    private const string LockTimeoutSql = """
        set local lock_timeout = '5s';
        """;

    private const string ExpandDeadlineSql = """
        alter table safety.photo_review_cases drop constraint ck_photo_review_cases_deadline;
        alter table safety.photo_review_cases add constraint ck_photo_review_cases_deadline check (isfinite(awaiting_review_at)
          and (review_deadline = awaiting_review_at + interval '168 hours'
            or review_deadline = awaiting_review_at + interval '72 hours'));
        """;

    internal const string UpSql = LockTimeoutSql + ExpandDeadlineSql;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve review cases; the previous release writes 72 h deadlines that this expanded constraint accepts.");
}
