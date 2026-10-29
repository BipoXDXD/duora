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
    // Decisão do dono de 29/10/2026 (#173, ADR-0035): a fila espera 7 dias, não mais 72 h.
    private static readonly TimeSpan ReviewWait = TimeSpan.FromDays(7);

    [Fact]
    public async Task ReviewExpirationUsesTheExactDeadlineAndCommitsOnlyASystemResultAndCleanupObligation()
    {
        var item = await CaseAsync();
        var files = Files();
        _time.Advance(TimeSpan.FromDays(7).Subtract(TimeSpan.FromTicks(1)));
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
    public async Task ANewCaseIsStillAwaitingReviewAtTheFormerSeventyTwoHourDeadline()
    {
        var item = await CaseAsync();
        var projections = await ExpirationProjectionAsync();
        _time.Advance(TimeSpan.FromHours(72));

        Assert.Equal(0, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));

        Assert.Equal(projections, await ExpirationProjectionAsync());
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from safety.photo_review_cases r join media.review_candidates c on c.case_id=r.id
            where r.id=$1 and r.review_deadline=r.awaiting_review_at+interval '168 hours'
              and c.retention_until=r.review_deadline+interval '24 hours'
            """, item.CaseId));
    }

    [Fact]
    public async Task ACaseOpenedByThePreviousReleaseKeepsItsSeventyTwoHourDeadline()
    {
        var item = await CaseAsync();
        // The release before 29/10/2026 wrote deadline = start + 72 h and candidate retention = start + 96 h.
        await ExecuteAsync("""
            update safety.photo_review_cases set review_deadline=awaiting_review_at+interval '72 hours' where id=$1
            """, item.CaseId);
        await ExecuteAsync("""
            update media.review_candidates c set retention_until=r.review_deadline+interval '24 hours'
            from safety.photo_review_cases r where r.id=c.case_id and r.id=$1
            """, item.CaseId);
        _time.Advance(TimeSpan.FromHours(72).Subtract(TimeSpan.FromTicks(1)));
        Assert.Equal(0, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        _time.Advance(TimeSpan.FromTicks(1));

        Assert.Equal(1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));

        await AssertExpiredWithSystemProofAsync(item);
    }

    [Fact]
    public async Task ReviewExpirationKeepsTheProducerRetentionContractAndRejectsADivergentSnapshot()
    {
        var item = await CaseAsync();
        var generation = await CleanupGenerationAsync(item);
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates c join safety.photo_review_cases r on r.id=c.case_id
            where c.generation=$1 and c.retention_until=r.review_deadline+interval '24 hours'
            """, generation));
        await ExecuteAsync("update media.review_candidates set retention_until=retention_until-interval '1 second' where generation=$1", generation);
        _time.Advance(ReviewWait + TimeSpan.FromHours(1));
        var before = await ExpirationProjectionAsync();
        var files = Files();

        Assert.Equal(0, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));

        Assert.Equal(before, await ExpirationProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task ReviewExpirationKeepsTheEffectMarkerWhenTheAuditClockAdvancesBeforeAppend()
    {
        var item = await CaseAsync();
        _time.Advance(ReviewWait);
        var effectAt = _time.GetUtcNow();
        var advance = new AdvanceExpirationAuditClock(_time);

        Assert.Equal(1, await new PhotoReviewExpirations(Factory(advance), _time).ExpireOnceAsync(Ct));

        Assert.True(advance.Fired);
        await AssertExpiredWithSystemProofAsync(item, effectAt);
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates c join ops.audit_events e on e.id=c.cleanup_audit_id
            where c.case_id=$1 and c.cleanup_requested_at=$2 and e.occurred_at=$3
            """, item.CaseId, effectAt, _time.GetUtcNow()));
        Assert.Equal(effectAt.AddSeconds(1), _time.GetUtcNow());
    }

    [Fact]
    public async Task ConcurrentReviewExpirationsCommitExactlyOneResultAndAudit()
    {
        var item = await CaseAsync();
        _time.Advance(ReviewWait + TimeSpan.FromHours(1));
        var results = await Task.WhenAll(
            new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct),
            new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));

        Assert.Equal(1, results.Sum());
        await AssertExpiredWithSystemProofAsync(item);
    }

    [Fact]
    public async Task ReviewExpirationSkipsADivergentOldestCaseWithoutStarvingLaterDeadlines()
    {
        var oldest = await CaseAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var next = await CaseAsync();
        _time.Advance(ReviewWait + TimeSpan.FromHours(1));
        await ExecuteAsync("update safety.photo_review_cases set candidate_generation=$2 where id=$1", oldest.CaseId, Guid.NewGuid());
        var before = await ExpirationCaseProjectionAsync(oldest);
        var files = Files();

        Assert.Equal(1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));

        await AssertExpiredWithSystemProofAsync(next);
        Assert.Equal(before, await ExpirationCaseProjectionAsync(oldest));
        Assert.Equal(files, Files());
        Assert.Equal(0, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        Assert.Equal(before, await ExpirationCaseProjectionAsync(oldest));
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
        var projections = await ExpirationProjectionAsync();
        _time.Advance(ReviewWait + TimeSpan.FromHours(1));

        Assert.Equal(0, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        Assert.Equal(files, Files());
        Assert.Equal(history, await CleanupHistoryAsync());
        Assert.Equal(candidate, await CandidateMetadataAsync(item));
        Assert.Equal(projections, await ExpirationProjectionAsync());
    }

    [Fact]
    public async Task ReviewExpirationRollsBackStateVersionsAndCleanupWhenItsAuditFails()
    {
        var item = await CaseAsync();
        var candidate = await CandidateMetadataAsync(item);
        var files = Files();
        _time.Advance(ReviewWait);
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
        _time.Advance(ReviewWait);
        var result = await ExpireWhileGuardedAsync(item, (_, _) =>
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        });

        Assert.Equal(1, result);
        await AssertExpiredWithSystemProofAsync(item);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewExpirationRevalidatesTheWinnerAfterDiscoveryAndARealGuardWait(bool generationChanged)
    {
        var item = await CaseAsync();
        var files = Files();
        _time.Advance(ReviewWait);
        string? winner = null;
        var result = await ExpireWhileGuardedAsync(item, async (connection, transaction) =>
        {
            // Fixture mutation under the same actual guard; the future owner HTTP command is not simulated as implemented.
            var sql = generationChanged
                ? "update safety.photo_review_cases set candidate_generation=$2,version=version+1 where id=$1"
                : "update media.uploads set state='Deleted',version=version+1 where id=$1";
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 2 };
            command.Parameters.AddWithValue(generationChanged ? item.CaseId : item.UploadId);
            if (generationChanged)
            {
                command.Parameters.AddWithValue(Guid.NewGuid());
            }

            Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
            winner = await ReadExpirationProjectionAsync(connection, transaction, Ct);
        });

        Assert.Equal(0, result);
        Assert.NotNull(winner);
        Assert.Equal(winner, await ExpirationProjectionAsync());
        Assert.Equal(files, Files());
        Assert.Equal(0, await ScalarAsync("select count(*) from ops.audit_events where action='safety.photo_review_expired'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewExpirationRecoversAnUncertainCommitWithoutConfirmingOrRepeatingIt(bool afterCommit)
    {
        var item = await CaseAsync();
        var before = await ExpirationProjectionAsync();
        var files = Files();
        _time.Advance(ReviewWait);
        var fault = new ExpirationCommitFault(_connectionString, item.UploadId, afterCommit);

        await Assert.ThrowsAsync<IOException>(() => new PhotoReviewExpirations(Factory(fault), _time).ExpireOnceAsync(Ct));

        Assert.True(fault.Fired);
        if (afterCommit)
        {
            await AssertExpiredWithSystemProofAsync(item);
        }
        else
        {
            Assert.Equal(before, await ExpirationProjectionAsync());
        }

        Assert.Equal(afterCommit ? 0 : 1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        await AssertExpiredWithSystemProofAsync(item);
        Assert.Equal(files, Files());
    }

    private async Task<int> ExpireWhileGuardedAsync(CaseData item,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> beforeRelease)
    {
        await ExecuteAsync("insert into safety.user_guards(user_id) values($1) on conflict do nothing", item.OwnerId);
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
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var expiration = new PhotoReviewExpirations(new ContextFactory(options.Options), _time).ExpireOnceAsync(attempt.Token);
        var released = false;
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
            await beforeRelease(locker, held);
            await held.CommitAsync(Ct);
            released = true;
            return await expiration.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (!released)
                {
                    await held.RollbackAsync(cleanup.Token);
                }
            }
            finally
            {
                await attempt.CancelAsync();
                try
                {
                    // Cancel the producer and observe it, not just a timeout wrapper over its task.
                    await expiration;
                }
                catch (OperationCanceledException) when (expiration.IsCompleted && expiration.IsCanceled && attempt.IsCancellationRequested)
                {
                }
            }
        }
    }

    private async Task<string> ExpirationProjectionAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        return await ReadExpirationProjectionAsync(connection, null, Ct);
    }

    private async Task<string> ExpirationCaseProjectionAsync(CaseData item)
    {
        await using var db = Context();
        return await db.Database.SqlQuery<string>($"""
            select jsonb_build_array(
              (select to_jsonb(u) from media.uploads u where u.id={item.UploadId}),
              (select to_jsonb(r) from safety.photo_review_cases r where r.id={item.CaseId}),
              (select coalesce(jsonb_agg(to_jsonb(c) order by c.generation),'[]'::jsonb)
                from media.review_candidates c where c.upload_id={item.UploadId}),
              (select coalesce(jsonb_agg(to_jsonb(e) order by e.id),'[]'::jsonb)
                from ops.audit_events e where e.target_type='photo_review_case' and e.target_id={item.CaseId})
            )::text as "Value"
            """).SingleAsync(Ct);
    }

    private static async Task<string> ReadExpirationProjectionAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select jsonb_build_array(
              (select coalesce(jsonb_agg(to_jsonb(u) order by u.id),'[]'::jsonb) from media.uploads u),
              (select coalesce(jsonb_agg(to_jsonb(r) order by r.id),'[]'::jsonb) from safety.photo_review_cases r),
              (select coalesce(jsonb_agg(to_jsonb(a) order by a.id),'[]'::jsonb) from media.media_assets a),
              (select coalesce(jsonb_agg(to_jsonb(v) order by v.media_asset_id,v.kind),'[]'::jsonb) from media.media_variants v),
              (select coalesce(jsonb_agg(to_jsonb(c) order by c.generation),'[]'::jsonb) from media.review_candidates c),
              (select coalesce(jsonb_agg(to_jsonb(e) order by e.id),'[]'::jsonb) from ops.audit_events e)
            )::text
            """, connection, transaction);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed class ExpirationCommitFault(string connectionString, Guid uploadId, bool afterCommit) : DbTransactionInterceptor
    {
        internal bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && !Fired && await HasExpiredAsync(Assert.IsType<NpgsqlConnection>(transaction.Connection),
                Assert.IsType<NpgsqlTransaction>(transaction), cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic failure before expiration commit.");
            }

            return result;
        }

        public override async Task TransactionCommittedAsync(DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!afterCommit || Fired)
            {
                return;
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            if (await HasExpiredAsync(connection, null, cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic lost expiration commit response.");
            }
        }

        private async Task<bool> HasExpiredAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("""
                select exists(select 1 from media.uploads where id=$1 and state='Rejected' and rejection_code='PHOTO_REVIEW_EXPIRED')
                """, connection, transaction);
            command.Parameters.AddWithValue(uploadId);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
    }

    private async Task AssertExpiredWithSystemProofAsync(CaseData item, DateTimeOffset? effectAt = null)
    {
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.uploads u join safety.photo_review_cases r on r.upload_id=u.id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            join ops.audit_events e on e.id=c.cleanup_audit_id
            where u.id=$1 and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_EXPIRED' and u.version=$2
              and r.version=2 and r.review_deadline<=$3 and c.committed_at is not null
              and c.writer_settled_at is not null and c.cleanup_requested_at=$3 and c.cleanup_available_at<=$3
              and c.cleanup_token is null and c.deleted_at is null and c.retention_until=r.review_deadline+interval '24 hours'
              and e.actor_kind='System' and e.actor_id is null and e.action='safety.photo_review_expired'
              and e.target_type='photo_review_case' and e.target_id=r.id and e.target_version=r.version
            """, item.UploadId, item.UploadVersion + 1, effectAt ?? _time.GetUtcNow()));
        Assert.Equal(1, await ScalarAsync("select count(*) from ops.audit_events where action='safety.photo_review_expired' and target_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    private sealed class AdvanceExpirationAuditClock(Microsoft.Extensions.Time.Testing.FakeTimeProvider time) : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("update media.uploads set state='Rejected'", StringComparison.Ordinal))
            {
                Fired = true;
                time.Advance(TimeSpan.FromSeconds(1));
            }

            return ValueTask.FromResult(result);
        }
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
