using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentAppealDecisionPreservesTheOriginalFactAndCommitsOneReceipt(bool approve)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var original = await OriginalAppealFactAsync(data.Data.DecisionId);
        var key = Key();
        var result = Assert.IsType<PhotoReviewAppealDecisionResult.Decided>(await AppealDecisions().DecideAsync(
            data.Reviewer, data.Appeal.AppealId, 2, approve, key, Ct));
        Assert.Equal(3, result.AppealVersion);
        Assert.Equal(approve ? "approve" : "reject", result.Decision);
        Assert.Equal(approve, result.AssetId is not null);
        Assert.Equal(result, Assert.IsType<PhotoReviewAppealDecisionResult.Decided>(await AppealDecisions().DecideAsync(
            data.Reviewer, data.Appeal.AppealId, 2, approve, key, Ct)));
        Assert.IsType<PhotoReviewAppealDecisionResult.KeyReused>(await AppealDecisions().DecideAsync(
            data.Reviewer, data.Appeal.AppealId, 3, !approve, key, Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeal_decision_commands where appeal_id=$1", result.AppealId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where id=$1 and appeal_id is null and decision='reject'", data.Data.DecisionId));
        Assert.Equal(original, await OriginalAppealFactAsync(data.Data.DecisionId));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_decisions d join safety.photo_review_appeals a on a.id=d.appeal_id
            join media.review_candidates c on c.generation=d.candidate_generation
            where a.id=$1 and d.original_decision_id=a.original_decision_id and d.case_version=$2
              and d.actor_id=$3 and d.actor_id<>(select actor_id from safety.photo_review_decisions where id=a.original_decision_id)
              and a.closed_at=d.decided_at and a.version=3 and c.cleanup_requested_at=d.decided_at
              and c.retention_until=d.candidate_retention_until_snapshot
              and c.retention_until<=a.maximum_retention_until
            """, result.AppealId, result.CaseVersion, data.Reviewer.UserId));
        Assert.IsType<PhotoReviewAppealRequestResult.StateConflict>(await AppealRequests().RequestAsync(
            data.Data.Owner, data.Data.Item.UploadId, result.PhotoVersion, Key(), Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealDecisionAtTheHardLimitCannotCreateAHumanFactOrAsset(bool approve)
    {
        var data = await AssignedAppealForEvidenceAsync();
        _time.SetUtcNow(data.Appeal.MaximumRetentionUntil);
        data = data with { Reviewer = await FreshAppealAssignerSessionAsync(data.Reviewer.UserId) };
        var files = Files();
        Assert.IsType<PhotoReviewAppealDecisionResult.StateConflict>(await AppealDecisions().DecideAsync(
            data.Reviewer, data.Appeal.AppealId, 2, approve, Key(), Ct));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", data.Data.Item.UploadId));
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppealDecisionRequiresTheDistinctAssignedReviewerAndFreshTotp(bool originalActor)
    {
        var data = await AssignedAppealForEvidenceAsync();
        if (!originalActor)
        {
            await ExecuteAsync("update identity.login_sessions set totp_verified_at=null where id=$1", data.Reviewer.SessionId);
        }
        var result = await AppealDecisions().DecideAsync(originalActor ? data.Original : data.Reviewer,
            data.Appeal.AppealId, 2, false, Key(), Ct);
        Assert.True(result is PhotoReviewAppealDecisionResult.Forbidden or PhotoReviewAppealDecisionResult.NotFound);
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
    }

    [Fact]
    public async Task MissingAppealEvidenceDoesNotBecomeAHumanRejection()
    {
        var data = await AssignedAppealForEvidenceAsync();
        await using var db = Context();
        var generation = await db.Database.SqlQuery<Guid>($"select candidate_generation as \"Value\" from safety.photo_review_appeals where id={data.Appeal.AppealId}").SingleAsync(Ct);
        await new FileSystemObjectStore(_root.FullName).DeleteAsync(ObjectKey.ForReviewCandidate(generation, PhotoVariant.Full), Ct);
        Assert.IsType<PhotoReviewAppealDecisionResult.Unavailable>(await AppealDecisions().DecideAsync(
            data.Reviewer, data.Appeal.AppealId, 2, false, Key(), Ct));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
    }

    [Fact]
    public async Task ConcurrentIndependentAppealCommandsCommitOnlyOneDecision()
    {
        var data = await AssignedAppealForEvidenceAsync();
        var results = await Task.WhenAll(
            AppealDecisions().DecideAsync(data.Reviewer, data.Appeal.AppealId, 2, false, Key(), Ct),
            AppealDecisions().DecideAsync(data.Reviewer, data.Appeal.AppealId, 2, false, Key(), Ct));
        Assert.Single(results.OfType<PhotoReviewAppealDecisionResult.Decided>());
        Assert.Single(results, r => r is PhotoReviewAppealDecisionResult.NotFound
            or PhotoReviewAppealDecisionResult.StateConflict or PhotoReviewAppealDecisionResult.PreconditionFailed);
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeal_decision_commands where appeal_id=$1", data.Appeal.AppealId));
    }

    [Fact]
    public async Task RevocationDuringAppealPromotionPreventsTheCommitAndRetainsCleanup()
    {
        var data = await AssignedAppealForEvidenceAsync();
        var revoked = false;
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName), afterPut: async _ =>
        {
            if (!revoked)
            {
                revoked = true;
                await GrantAsync(data.Reviewer.UserId, PhotoReviewCapability.Appeal, false);
            }
        });
        var result = await new PhotoReviewAppealDecisions(Factory(), store, _time).DecideAsync(
            data.Reviewer, data.Appeal.AppealId, 2, true, Key(), Ct);
        Assert.True(revoked);
        Assert.IsType<PhotoReviewAppealDecisionResult.Forbidden>(result);
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", data.Data.Item.UploadId));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.review_promotions where appeal_id=$1 and committed_at is null and cleanup_requested_at is not null", data.Appeal.AppealId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainAppealApprovalCommitIsConfirmedOnlyByAuthenticatedReceiptReplay(bool afterCommit)
    {
        var data = await AssignedAppealForEvidenceAsync();
        var fault = new ApprovalCommitFault(_connectionString, data.Data.Item.UploadId, afterCommit);
        var key = Key();
        var service = new PhotoReviewAppealDecisions(Factory(fault), new FileSystemObjectStore(_root.FullName), _time);
        await Assert.ThrowsAsync<IOException>(() => service.DecideAsync(data.Reviewer, data.Appeal.AppealId, 2, true, key, Ct));
        Assert.Equal(afterCommit ? 1 : 0, await ScalarAsync("select count(*) from safety.photo_review_decisions where appeal_id=$1", data.Appeal.AppealId));
        var replay = Assert.IsType<PhotoReviewAppealDecisionResult.Decided>(await AppealDecisions().DecideAsync(
            data.Reviewer, data.Appeal.AppealId, 2, true, key, Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_appeal_decision_commands where appeal_id=$1", replay.AppealId));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", data.Data.Item.UploadId));
    }

    private PhotoReviewAppealDecisions AppealDecisions() => new(Factory(), new FileSystemObjectStore(_root.FullName), _time);
}
