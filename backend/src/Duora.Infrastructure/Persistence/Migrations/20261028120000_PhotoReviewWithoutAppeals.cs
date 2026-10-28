using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Duora.Infrastructure.Persistence.Migrations;

/// <summary>
/// Decisão do dono de 2026-10-28: a revisão de fotos não tem recurso; uma rejeição libera o candidato para a mesma
/// limpeza da aprovação e o titular pode enviar uma foto nova. Expand: a constraint passa a aceitar também a rejeição
/// com 24 h, mantendo os 168 h que a release anterior grava, e nenhuma tabela, coluna ou privilégio de recurso é
/// removido, para o rollback continuar lendo e escrevendo o schema dele. O contract (remover as tabelas de recurso, as
/// colunas appeal_* e os 168 h) fica para uma migração posterior, depois que nenhuma release em uso as conhecer.
/// </summary>
[DbContext(typeof(DuoraDbContext))]
[Migration("20261028120000_PhotoReviewWithoutAppeals")]
public sealed class PhotoReviewWithoutAppeals : Migration
{
    /// <summary>
    /// HumanReview nunca foi admitida fora de Development/Testing, então nenhum ambiente persistente tem recurso. Um
    /// recurso existente ficaria sem código que o encerre ou limpe o candidato: a migração falha em vez de abandoná-lo.
    /// </summary>
    internal const string RefuseExistingAppealsSql = """
        do $$ begin
          if exists(select 1 from safety.photo_review_appeals) then
            raise exception 'safety.photo_review_appeals tem linhas: a release sem recurso não as encerra. Resolva-as com a release anterior antes de migrar.';
          end if;
        end $$;
        """;

    /// <summary>
    /// Rejeições gravadas pela release anterior guardavam o candidato 168 h à espera de um recurso, sem obrigação de
    /// limpeza. Sem recurso, elas recebem a mesma obrigação que a rejeição nova grava: o marcador igual ao instante da
    /// decisão, que o DecidedCandidateCleanupDispatcher só aceita quando a decisão imutável o explica.
    /// </summary>
    internal const string RequestCleanupOfExistingRejectionsSql = """
        update media.review_candidates c
        set cleanup_requested_at=d.decided_at,cleanup_available_at=least(c.cleanup_available_at,d.decided_at)
        from safety.photo_review_decisions d join safety.photo_review_cases r on r.id=d.case_id
        where d.case_id=c.case_id and d.candidate_generation=c.generation and r.candidate_generation=c.generation
          and r.upload_id=c.upload_id and d.appeal_id is null and d.policy_version=r.policy_version
          and d.decision='reject' and d.reason_code='does_not_meet_policy'
          and d.candidate_retention_until_snapshot=c.retention_until
          and c.committed_at is not null and c.cleanup_requested_at is null and c.deleted_at is null;
        """;

    private const string ExpandCandidateRetentionSql = """
        alter table safety.photo_review_decisions drop constraint ck_photo_review_decisions_candidate_retention;
        alter table safety.photo_review_decisions add constraint ck_photo_review_decisions_candidate_retention check
          (isfinite(candidate_retention_until_snapshot) and
            ((appeal_id is null and candidate_retention_until_snapshot=decided_at+interval '24 hours')
            or (appeal_id is null and decision='reject' and candidate_retention_until_snapshot=decided_at+interval '168 hours')
            or (appeal_id is not null and candidate_retention_until_snapshot>decided_at
              and candidate_retention_until_snapshot<=decided_at+interval '24 hours')));
        """;

    /// <summary>Falha rápido em vez de enfileirar o tráfego atrás do ACCESS EXCLUSIVE do ALTER; o migrator pode repetir.</summary>
    private const string LockTimeoutSql = """
        set local lock_timeout = '5s';
        """;

    internal const string UpSql =
        LockTimeoutSql + RefuseExistingAppealsSql + ExpandCandidateRetentionSql + RequestCleanupOfExistingRejectionsSql;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Preserve decisions and cleanup obligations; the previous release reads this expanded constraint as is.");
}
