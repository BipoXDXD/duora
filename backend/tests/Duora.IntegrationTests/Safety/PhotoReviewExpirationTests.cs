using System.Data.Common;
using System.Transactions;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    public async Task ReviewExpirationUsesTheExactDeadlineAndCommitsOnlyASystemResultAndCleanupObligation()
    {
        var item = await CaseAsync();
        var files = Files();
        _time.Advance(TimeSpan.FromHours(72).Subtract(TimeSpan.FromTicks(1)));
        Assert.Equal(0, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        Assert.Equal(files, Files());
        _time.Advance(TimeSpan.FromTicks(1));

        Assert.Equal(1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));

        await AssertExpiredWithSystemProofAsync(item);
        Assert.Equal(files, Files());
        Assert.Equal(0, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        await AssertExpiredWithSystemProofAsync(item);
    }

    [Fact]
    public async Task ConcurrentReviewExpirationsCommitExactlyOneResultAndAudit()
    {
        var item = await CaseAsync();
        _time.Advance(TimeSpan.FromHours(73));
        var results = await Task.WhenAll(
            new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct),
            new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));

        Assert.Equal(1, results.Sum());
        await AssertExpiredWithSystemProofAsync(item);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("deleted")]
    public async Task ReviewExpirationCannotRewriteAnExistingFinalResult(string finalState)
    {
        var (reviewer, item) = await PreparedAsync();
        if (finalState == "approved")
        {
            Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        }
        else if (finalState == "rejected")
        {
            Assert.IsType<PhotoReviewRejectionResult.Rejected>(await new PhotoReviewRejections(Factory(),
                new Duora.Infrastructure.Media.FileSystemObjectStore(_root.FullName), _time).RejectAsync(
                    reviewer, item.CaseId, 2, PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
        }
        else
        {
            // Synthetic fixture for the existing Deleted state; the owner HTTP command is a separate slice.
            await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=$1", item.UploadId);
        }

        var files = Files();
        var history = await CleanupHistoryAsync();
        var candidate = await CandidateMetadataAsync(item);
        _time.Advance(TimeSpan.FromHours(73));

        Assert.Equal(0, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        Assert.Equal(files, Files());
        Assert.Equal(history, await CleanupHistoryAsync());
        Assert.Equal(candidate, await CandidateMetadataAsync(item));
    }

    [Fact]
    public async Task ReviewExpirationRollsBackStateVersionsAndCleanupWhenItsAuditFails()
    {
        var item = await CaseAsync();
        var candidate = await CandidateMetadataAsync(item);
        var files = Files();
        _time.Advance(TimeSpan.FromHours(72));
        var fault = new RefuseExpirationAudit();

        await Assert.ThrowsAsync<IOException>(() => new PhotoReviewExpirations(Factory(fault), _time).ExpireOnceAsync(Ct));

        Assert.True(fault.Fired);
        Assert.Equal(candidate, await CandidateMetadataAsync(item));
        Assert.Equal(files, Files());
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview' and version=$2",
            item.UploadId, item.UploadVersion));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_cases where id=$1 and version=1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where action='safety.photo_review_expired'"));
        Assert.Equal(1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        await AssertExpiredWithSystemProofAsync(item);
    }

    [Fact]
    public async Task ReviewExpirationCannotJoinAnAmbientTransaction()
    {
        using var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
    }

    [Fact]
    public async Task ReviewExpirationReadsTheEffectClockAfterWaitingForTheOwnersGuard()
    {
        var item = await CaseAsync();
        await ExecuteAsync("insert into safety.user_guards(user_id) values($1) on conflict do nothing", item.OwnerId);
        _time.Advance(TimeSpan.FromHours(72));
        await using var locker = new NpgsqlConnection(_connectionString);
        await locker.OpenAsync(Ct);
        await using var held = await locker.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand("select user_id from safety.user_guards where user_id=$1 for update", locker, held))
        {
            command.Parameters.AddWithValue(item.OwnerId);
            Assert.Equal(item.OwnerId, await command.ExecuteScalarAsync(Ct));
        }

        var label = "expiry-" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = label }.ConnectionString);
        var expiration = new PhotoReviewExpirations(new ContextFactory(options.Options), _time).ExpireOnceAsync(Ct);
        try
        {
            var waiting = false;
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            observation.CancelAfter(TimeSpan.FromSeconds(5));
            while (!expiration.IsCompleted && !observation.IsCancellationRequested)
            {
                waiting = await ScalarAsync("select count(*) from pg_stat_activity where application_name=$1 and wait_event_type='Lock'", label) == 1;
                if (waiting)
                {
                    break;
                }

                await Task.Delay(10, observation.Token);
            }

            Assert.True(waiting, "The expiry effect must serialize with the owner's existing Safety guard.");
            _time.Advance(TimeSpan.FromSeconds(10));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await held.RollbackAsync(cleanup.Token);
            await expiration.WaitAsync(cleanup.Token);
        }

        Assert.Equal(1, await expiration);
        await AssertExpiredWithSystemProofAsync(item);
    }

    private async Task AssertExpiredWithSystemProofAsync(CaseData item)
    {
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.uploads u join safety.photo_review_cases r on r.upload_id=u.id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            join ops.audit_events e on e.id=c.cleanup_audit_id
            where u.id=$1 and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_EXPIRED' and u.version=$2
              and r.version=2 and r.review_deadline<=$3 and c.committed_at is not null
              and c.writer_settled_at is not null and c.cleanup_requested_at=$3 and c.cleanup_available_at<=$3
              and c.cleanup_token is null and c.deleted_at is null and c.retention_until=r.review_deadline
              and e.actor_kind='System' and e.actor_id is null and e.action='safety.photo_review_expired'
              and e.target_type='photo_review_case' and e.target_id=r.id and e.target_version=r.version
            """, item.UploadId, item.UploadVersion + 1, _time.GetUtcNow()));
        Assert.Equal(1, await ScalarAsync("select count(*) from ops.audit_events where action='safety.photo_review_expired' and target_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    private sealed class RefuseExpirationAudit : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is "safety.photo_review_expired"))
            {
                Fired = true;
                throw new IOException("Synthetic expiration audit failure.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
