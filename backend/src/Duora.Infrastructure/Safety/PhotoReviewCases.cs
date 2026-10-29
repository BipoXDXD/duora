using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

/// <summary>Safety owns the case and audit; its caller coordinates the upload in the same transaction.</summary>
internal static class PhotoReviewCases
{
    /// <summary>
    /// Espera máxima na fila (ADR-0035, decisão de 29/10/2026). Vencida, o caso expira como PHOTO_REVIEW_EXPIRED,
    /// nunca como aprovação. O prazo é gravado por linha: casos abertos com 72 h pela release anterior o mantêm.
    /// </summary>
    internal static readonly TimeSpan ReviewWait = TimeSpan.FromDays(7);

    /// <summary>Folga para limpar o candidato depois do prazo; os coletores exigem retention = deadline + 24 h.</summary>
    internal static readonly TimeSpan CandidateCleanupAfterDeadline = TimeSpan.FromHours(24);

    internal static async Task RequestAsync(
        DuoraDbContext db, TimeProvider time, Guid caseId, Guid uploadId, Guid generation, long version,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A review case requires the coordinated media transaction.");
        }

        var inserted = await db.Database.ExecuteSqlAsync($"""
            insert into safety.photo_review_cases
              (id,upload_id,candidate_generation,policy_version,awaiting_review_at,review_deadline,version)
            select {caseId},{uploadId},{generation},'human-review-v1',{now},{now.Add(ReviewWait)},1
            from media.review_candidates c where c.generation={generation} and c.upload_id={uploadId}
              and c.upload_version={version} and c.committed_at is null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.deleted_at is null and c.prepare_deadline>{now}
            """, cancellationToken);
        if (inserted != 1)
        {
            throw new InvalidOperationException("The review case requires its matching live private candidate.");
        }

        await new AuditTrail(db, time).AppendAsync(new AuditRecord(
            AuditActorKind.System, null, AuditAction.PhotoReviewRequested, AuditTargetTypes.PhotoReviewCase,
            caseId, targetVersion: 1), cancellationToken);
    }
}
