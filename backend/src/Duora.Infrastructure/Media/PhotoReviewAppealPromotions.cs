using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Media;

internal sealed record ReviewAppealPromotionRequest(Guid AppealId, long AppealVersion, Guid OriginalDecisionId,
    Guid ReviewerId, ReviewPromotionRequest Candidate);

internal sealed partial class PhotoReviewPromotions
{
    // A separate, closed query: the initial preparer continues to accept only AwaitingReview.
    private async Task<Snapshot?> LoadAppealAsync(ReviewAppealPromotionRequest request, Guid generation, CancellationToken ct)
    {
        var candidate = request.Candidate;
        var upload = await db.Database.SqlQuery<Guid>($"select upload_id as \"Value\" from safety.photo_review_cases where id={candidate.CaseId}")
            .SingleOrDefaultAsync(ct);
        if (upload == Guid.Empty || request.AppealId == Guid.Empty || request.AppealVersion < 2)
        {
            return null;
        }
        await db.Database.ExecuteSqlAsync($"select 1 from media.uploads where id={upload} for share", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_cases where id={candidate.CaseId} for share", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from media.review_candidates where generation={candidate.CandidateGeneration} for share", ct);
        await db.Database.ExecuteSqlAsync($"select 1 from safety.photo_review_appeals where id={request.AppealId} for share", ct);
        var now = time.GetUtcNow();
        var state = await db.Database.SqlQuery<CandidateState>($"""
            select u.id as "UploadId",u.owner_id as "OwnerId",u.version as "UploadVersion",r.version as "CaseVersion",
              c.generation as "CandidateGeneration",r.policy_version as "PolicyVersion",c.detected_type as "DetectedType",
              c.width as "Width",c.height as "Height",a.maximum_retention_until as "ReviewDeadline",c.retention_until as "RetentionUntil"
            from safety.photo_review_appeals a join safety.photo_review_cases r on r.id=a.case_id
            join media.uploads u on u.id=r.upload_id join identity.users owner on owner.id=u.owner_id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            join safety.photo_review_decisions d on d.id=a.original_decision_id and d.case_id=r.id
              and d.candidate_generation=c.generation and d.policy_version=r.policy_version
            join safety.photo_review_appeal_assignments s on s.appeal_id=a.id and s.appeal_version=a.version
            where a.id={request.AppealId} and a.version={request.AppealVersion} and a.status='Open' and a.closed_at is null
              and d.id={request.OriginalDecisionId} and d.actor_id<>{request.ReviewerId} and s.reviewer_id={request.ReviewerId}
              and d.appeal_id is null and d.decision='reject' and d.reason_code='does_not_meet_policy'
              and d.case_version=r.version-1 and a.original_rejected_at=d.decided_at
              and a.requested_at>=d.decided_at and a.requested_at<d.decided_at+interval '168 hours'
              and a.response_target_at=a.requested_at+interval '168 hours'
              and a.maximum_retention_until=d.decided_at+interval '720 hours'
              and {now}>=a.requested_at and {now}<a.maximum_retention_until
              and a.owner_id=u.owner_id and a.candidate_generation=c.generation and a.policy_version=r.policy_version
              and r.id={candidate.CaseId} and r.version={candidate.CaseVersion} and r.policy_version={candidate.PolicyVersion}
              and r.policy_version='human-review-v1' and u.version={candidate.UploadVersion}
              and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_REJECTED'
              and c.generation={candidate.CandidateGeneration} and c.upload_version=u.version-3
              and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null and c.cleanup_audit_id is null and c.deleted_at is null
              and c.cleanup_token is null and c.cleanup_lease_until is null and c.retention_until=a.maximum_retention_until
              and owner.email_confirmed and owner.status in ('PendingEligibility','Active','Restricted','Suspended')
            """).SingleOrDefaultAsync(ct);
        if (state is null)
        {
            return null;
        }
        // Every byte still has to match the immutable original rejection manifest, not only mutable metadata.
        var variants = await db.Database.SqlQuery<StoredVariant>($"""
            select v.kind as "Kind",v.object_key as "ObjectKey",v.width as "Width",v.height as "Height",
              v.size_bytes as "SizeBytes",v.sha256 as "Sha256"
            from media.review_candidate_variants v join safety.photo_review_decision_variants d
              on d.decision_id={request.OriginalDecisionId} and d.variant=v.kind and d.size_bytes=v.size_bytes and d.sha256=v.sha256
            where v.generation={candidate.CandidateGeneration} order by v.kind for share of v
            """).ToListAsync(ct);
        now = time.GetUtcNow();
        return state.ReviewDeadline > now && state.RetentionUntil > now ? Validate(state, variants, generation) : null;
    }
}
