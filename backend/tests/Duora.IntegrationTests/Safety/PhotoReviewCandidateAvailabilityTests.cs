using System.Data.Common;
using System.Transactions;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Theory]
    [InlineData(PhotoVariant.Thumb)]
    [InlineData(PhotoVariant.Card)]
    [InlineData(PhotoVariant.Full)]
    public async Task MissingCommittedCandidateCreatesOnlyASystemUnavailableFactAndDurableCleanup(PhotoVariant variant)
    {
        var item = await CaseAsync();
        await LoseCandidateVariantAsync(item, variant);
        var history = await AvailabilityHistoryAsync();
        var files = Files();

        Assert.Equal(1, await Availability().ReconcileAsync(item.CaseId, Ct));

        await AssertUnavailableCandidateAsync(item);
        Assert.Equal(history, await AvailabilityHistoryAsync());
        Assert.Equal(files, Files());
        var confirmed = await AllCleanupProjectionAsync();
        Assert.Equal(0, await Availability().ReconcileAsync(item.CaseId, Ct));
        Assert.Equal(confirmed, await AllCleanupProjectionAsync());
    }

    [Fact]
    public async Task CorruptCandidateOfTheSameSizeCannotRemainAwaitingReview()
    {
        var item = await CaseAsync();
        var store = new FileSystemObjectStore(_root.FullName);
        var key = ObjectKey.ForReviewCandidate(await CleanupGenerationAsync(item), PhotoVariant.Card);
        byte[] corrupted;
        await using (var stream = await store.OpenReadAsync(key, Ct))
        {
            Assert.NotNull(stream);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, Ct);
            corrupted = buffer.ToArray();
        }

        Assert.NotEmpty(corrupted);
        corrupted[0] ^= 1;
        await store.DeleteAsync(key, Ct);
        using (var content = new MemoryStream(corrupted))
        {
            await store.PutAsync(key, content, corrupted.Length, "image/webp", Ct);
        }

        var history = await AvailabilityHistoryAsync();
        var files = Files();
        Assert.Equal(1, await Availability().ReconcileAsync(item.CaseId, Ct));
        await AssertUnavailableCandidateAsync(item);
        Assert.Equal(history, await AvailabilityHistoryAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task HealthyCandidateIsReadOutsideBusinessLocksWithoutChangingAnyProjection()
    {
        var item = await CaseAsync();
        var generation = await CleanupGenerationAsync(item);
        var before = await AllCleanupProjectionAsync();
        var history = await AvailabilityHistoryAsync();
        var files = Files();
        var reads = 0;
        var store = new AvailabilityStore(new FileSystemObjectStore(_root.FullName), async (_, ct) =>
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            foreach (var (sql, id) in new[]
            {
                ("select id from media.uploads where id=$1 for update nowait", item.UploadId),
                ("select id from safety.photo_review_cases where id=$1 for update nowait", item.CaseId),
                ("select generation from media.review_candidates where generation=$1 for update nowait", generation),
            })
            {
                await using var probe = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 2 };
                probe.Parameters.AddWithValue(id);
                Assert.Equal(id, await probe.ExecuteScalarAsync(ct));
            }

            await transaction.RollbackAsync(ct);
            reads++;
        });

        Assert.Equal(0, await Availability(store).ReconcileAsync(item.CaseId, Ct));

        Assert.Equal(3, reads);
        Assert.Equal(before, await AllCleanupProjectionAsync());
        Assert.Equal(history, await AvailabilityHistoryAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task CandidateReconciliationDoesNotReadStorageForAnUnknownCase()
    {
        _ = await CaseAsync();
        var before = await AllCleanupProjectionAsync();
        var store = new AvailabilityStore(new FileSystemObjectStore(_root.FullName), (_, _) =>
            throw new InvalidOperationException("Unknown case must not read storage."));

        Assert.Equal(0, await Availability(store).ReconcileAsync(Guid.NewGuid(), Ct));
        Assert.Equal(before, await AllCleanupProjectionAsync());
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("deleted")]
    [InlineData("expired")]
    public async Task CandidateAvailabilityCannotRewriteATerminalWinner(string outcome)
    {
        var (reviewer, item) = await PreparedAsync();
        if (outcome == "approved")
        {
            Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        }
        else if (outcome == "rejected")
        {
            Assert.IsType<PhotoReviewRejectionResult.Rejected>(await new PhotoReviewRejections(Factory(),
                new FileSystemObjectStore(_root.FullName), _time).RejectAsync(reviewer, item.CaseId, 2,
                    PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
        }
        else if (outcome == "expired")
        {
            _time.Advance(ReviewWait + TimeSpan.FromHours(1));
            Assert.Equal(1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
        }
        else
        {
            await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=$1", item.UploadId);
        }

        await LoseCandidateVariantAsync(item, PhotoVariant.Full);
        var before = await AllCleanupProjectionAsync();
        var history = await AvailabilityHistoryAsync();
        var files = Files();
        Assert.Equal(0, await Availability().ReconcileAsync(item.CaseId, Ct));
        Assert.Equal(before, await AllCleanupProjectionAsync());
        Assert.Equal(history, await AvailabilityHistoryAsync());
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("retention")]
    [InlineData("policy")]
    [InlineData("version")]
    public async Task CandidateAvailabilityRequiresTheExactConfirmedContext(string drift)
    {
        var item = await CaseAsync();
        await LoseCandidateVariantAsync(item, PhotoVariant.Full);
        if (drift == "generation")
        {
            await ExecuteAsync("update safety.photo_review_cases set candidate_generation=$2 where id=$1", item.CaseId, Guid.NewGuid());
        }
        else if (drift == "retention")
        {
            await ExecuteAsync("update media.review_candidates set retention_until=retention_until+interval '1 second' where generation=$1", await CleanupGenerationAsync(item));
        }
        else if (drift == "policy")
        {
            await ExecuteAsync("update safety.photo_review_cases set policy_version='unknown-policy' where id=$1", item.CaseId);
        }
        else
        {
            await ExecuteAsync("update media.uploads set version=version+1 where id=$1", item.UploadId);
        }

        var before = await AllCleanupProjectionAsync();
        var history = await AvailabilityHistoryAsync();
        var files = Files();
        Assert.Equal(0, await Availability().ReconcileAsync(item.CaseId, Ct));
        Assert.Equal(before, await AllCleanupProjectionAsync());
        Assert.Equal(history, await AvailabilityHistoryAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task ConcurrentAvailabilityChecksCommitOneSystemFactAndOneObligation()
    {
        var item = await CaseAsync();
        await LoseCandidateVariantAsync(item, PhotoVariant.Full);
        var results = await Task.WhenAll(Availability().ReconcileAsync(item.CaseId, Ct), Availability().ReconcileAsync(item.CaseId, Ct));
        Assert.Single(results, value => value == 1);
        Assert.Single(results, value => value == 0);
        await AssertUnavailableCandidateAsync(item);
    }

    [Fact]
    public async Task AvailabilityAuditFailureRollsBackStateVersionsAndTheCleanupObligation()
    {
        var item = await CaseAsync();
        await LoseCandidateVariantAsync(item, PhotoVariant.Full);
        var before = await AllCleanupProjectionAsync();
        var files = Files();
        var fault = new RefuseAvailabilityAudit();

        await Assert.ThrowsAsync<IOException>(() => Availability(interceptors: [fault]).ReconcileAsync(item.CaseId, Ct));

        Assert.True(fault.Fired);
        Assert.Equal(before, await AllCleanupProjectionAsync());
        Assert.Equal(files, Files());
        Assert.Equal(1, await Availability().ReconcileAsync(item.CaseId, Ct));
        await AssertUnavailableCandidateAsync(item);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AvailabilityDoesNotConfirmUncertainCommitAndRepetitionObservesTheRealResult(bool afterCommit)
    {
        var item = await CaseAsync();
        await LoseCandidateVariantAsync(item, PhotoVariant.Full);
        var before = await AllCleanupProjectionAsync();
        var files = Files();
        var fault = new AvailabilityCommitFault(_connectionString, item.UploadId, afterCommit);

        await Assert.ThrowsAsync<IOException>(() => Availability(interceptors: [fault]).ReconcileAsync(item.CaseId, Ct));

        Assert.True(fault.Fired);
        if (!afterCommit)
        {
            Assert.Equal(before, await AllCleanupProjectionAsync());
        }

        Assert.Equal(afterCommit ? 0 : 1, await Availability().ReconcileAsync(item.CaseId, Ct));
        await AssertUnavailableCandidateAsync(item);
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AvailabilityCannotKeepAnOldSnapshotAcrossTheOwnersActualGuard(bool generationChanged)
    {
        var item = await CaseAsync();
        await LoseCandidateVariantAsync(item, PhotoVariant.Full);
        string? winner = null;
        var files = Files();
        var result = await AvailabilityWhileGuardedAsync(item, async (connection, transaction) =>
        {
            await using var change = new NpgsqlCommand(generationChanged
                ? "update safety.photo_review_cases set candidate_generation=$2 where id=$1"
                : "update media.uploads set state='Deleted',version=version+1 where id=$1", connection, transaction);
            change.Parameters.AddWithValue(generationChanged ? item.CaseId : item.UploadId);
            if (generationChanged)
            {
                change.Parameters.AddWithValue(Guid.NewGuid());
            }

            Assert.Equal(1, await change.ExecuteNonQueryAsync(Ct));
            winner = await ReadExpirationProjectionAsync(connection, transaction, Ct);
        });

        Assert.Equal(0, result);
        Assert.Equal(winner, await ExpirationProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("generation")]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task AvailabilityCannotOverrideAWinnerCommittedDuringStorageReads(string outcome)
    {
        var (reviewer, item) = await PreparedAsync();
        var fired = false;
        string? winner = null;
        string? history = null;
        string[]? files = null;
        var store = new AvailabilityStore(new FileSystemObjectStore(_root.FullName), async (_, _) =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            if (outcome == "approved")
            {
                Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
            }
            else if (outcome == "rejected")
            {
                Assert.IsType<PhotoReviewRejectionResult.Rejected>(await new PhotoReviewRejections(Factory(),
                    new FileSystemObjectStore(_root.FullName), _time).RejectAsync(reviewer, item.CaseId, 2,
                        PhotoReviewRejectionReason.DoesNotMeetPolicy, Key(), Ct));
            }
            else if (outcome == "generation")
            {
                await ExecuteAsync("update safety.photo_review_cases set candidate_generation=$2 where id=$1", item.CaseId, Guid.NewGuid());
            }
            else
            {
                await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=$1", item.UploadId);
            }

            await LoseCandidateVariantAsync(item, PhotoVariant.Full, useOriginalGeneration: true);
            winner = await AllCleanupProjectionAsync();
            history = await AvailabilityHistoryAsync();
            files = Files();
        });

        Assert.Equal(0, await Availability(store).ReconcileAsync(item.CaseId, Ct));

        Assert.True(fired);
        Assert.Equal(winner, await AllCleanupProjectionAsync());
        Assert.Equal(history, await AvailabilityHistoryAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task TransientStorageFailureDoesNotBecomeACommittedUnavailableJudgment()
    {
        var item = await CaseAsync();
        var before = await AllCleanupProjectionAsync();
        var files = Files();
        var store = new AvailabilityStore(new FileSystemObjectStore(_root.FullName), (_, _) =>
            throw new IOException("Synthetic transient read failure, not confirmed absence."));

        await Assert.ThrowsAsync<IOException>(() => Availability(store).ReconcileAsync(item.CaseId, Ct));

        Assert.Equal(before, await AllCleanupProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task CancelledAvailabilityCannotCommitAnyStateOrCleanupChange()
    {
        var item = await CaseAsync();
        await LoseCandidateVariantAsync(item, PhotoVariant.Full);
        var before = await AllCleanupProjectionAsync();
        var files = Files();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Availability().ReconcileAsync(item.CaseId, cancelled.Token));

        Assert.Equal(before, await AllCleanupProjectionAsync());
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task CandidateAvailabilityOwnsItsReadCommittedPhases()
    {
        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Availability().ReconcileAsync(Guid.NewGuid(), Ct));
    }

    private PhotoReviewCandidateAvailability Availability(IObjectStore? store = null, params IInterceptor[] interceptors) =>
        new(Factory(interceptors), store ?? new AvailabilityStore(new FileSystemObjectStore(_root.FullName), (_, _) => Task.CompletedTask), _time);

    private async Task LoseCandidateVariantAsync(CaseData item, PhotoVariant variant, bool useOriginalGeneration = false)
    {
        var generation = useOriginalGeneration
            ? await ReadGenerationForUploadAsync(item.UploadId)
            : await CleanupGenerationAsync(item);
        await new FileSystemObjectStore(_root.FullName).DeleteAsync(ObjectKey.ForReviewCandidate(generation, variant), Ct);
    }

    private async Task<Guid> ReadGenerationForUploadAsync(Guid uploadId)
    {
        await using var db = Context();
        return await db.Database.SqlQuery<Guid>($"select generation as \"Value\" from media.review_candidates where upload_id={uploadId}").SingleAsync(Ct);
    }

    private async Task AssertUnavailableCandidateAsync(CaseData item)
    {
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.uploads u join safety.photo_review_cases r on r.upload_id=u.id
            join media.review_candidates c on c.generation=r.candidate_generation and c.case_id=r.id and c.upload_id=u.id
            join ops.audit_events e on e.id=c.cleanup_audit_id
            where u.id=$1 and u.state='Rejected' and u.rejection_code='PHOTO_REVIEW_UNAVAILABLE' and u.version=$2
              and r.version=2 and c.committed_at is not null and c.writer_settled_at is not null
              and c.retention_until=r.review_deadline+interval '24 hours'
              and c.cleanup_requested_at is not null and c.cleanup_available_at<=c.cleanup_requested_at
              and c.cleanup_token is null and c.deleted_at is null
              and e.actor_kind='System' and e.actor_id is null and e.action='safety.photo_review_unavailable'
              and e.target_type='photo_review_case' and e.target_id=r.id and e.target_version=r.version
              and e.occurred_at>=c.cleanup_requested_at and e.occurred_at<=$3
            """, item.UploadId, item.UploadVersion + 1, _time.GetUtcNow()));
        Assert.Equal(1, await ScalarAsync("select count(*) from ops.audit_events where action='safety.photo_review_unavailable' and target_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    private async Task<string> AvailabilityHistoryAsync()
    {
        await using var db = Context();
        return await db.Database.SqlQuery<string>($"""
            select jsonb_build_array(
              (select coalesce(jsonb_agg(to_jsonb(d) order by d.id),'[]'::jsonb) from safety.photo_review_decisions d),
              (select coalesce(jsonb_agg(to_jsonb(v) order by v.decision_id,v.variant),'[]'::jsonb) from safety.photo_review_decision_variants v),
              (select coalesce(jsonb_agg(to_jsonb(c) order by c.actor_id,c.operation,c.key_hash),'[]'::jsonb) from safety.photo_review_decision_commands c)
            )::text as "Value"
            """).SingleAsync(Ct);
    }

    private async Task<int> AvailabilityWhileGuardedAsync(CaseData item,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> beforeRelease)
    {
        await ExecuteAsync("insert into safety.user_guards(user_id) values($1) on conflict do nothing", item.OwnerId);
        await using var locker = new NpgsqlConnection(_connectionString);
        await locker.OpenAsync(Ct);
        await using var held = await locker.BeginTransactionAsync(Ct);
        await using var hold = new NpgsqlCommand("select user_id from safety.user_guards where user_id=$1 for update", locker, held);
        hold.Parameters.AddWithValue(item.OwnerId);
        Assert.Equal(item.OwnerId, await hold.ExecuteScalarAsync(Ct));
        var label = "unavailable-guard-" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = label }.ConnectionString);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = new PhotoReviewCandidateAvailability(new ContextFactory(options.Options), new FileSystemObjectStore(_root.FullName), _time)
            .ReconcileAsync(item.CaseId, attempt.Token);
        var released = false;
        try
        {
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            observation.CancelAfter(TimeSpan.FromSeconds(5));
            var waiting = false;
            for (var count = 0; count < 100 && !request.IsCompleted; count++)
            {
                waiting = await ScalarAsync("select count(*) from pg_stat_activity where application_name=$1 and wait_event_type='Lock'", label) == 1;
                if (waiting)
                {
                    break;
                }

                await Task.Delay(10, observation.Token);
            }

            Assert.True(waiting, "Candidate availability must wait on the owner's actual Safety guard.");
            await beforeRelease(locker, held);
            await held.CommitAsync(Ct);
            released = true;
            return await request.WaitAsync(TimeSpan.FromSeconds(5), Ct);
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
                    await request;
                }
                catch (OperationCanceledException) when (request.IsCompleted && request.IsCanceled && attempt.IsCancellationRequested)
                {
                }
            }
        }
    }

    private sealed class AvailabilityStore(IObjectStore inner, Func<ObjectKey, CancellationToken, Task> beforeOpen) : IObjectStore
    {
        public async Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Assert.StartsWith("review-candidates/", key.Value, StringComparison.Ordinal);
            await beforeOpen(key, cancellationToken);
            return await inner.OpenReadAsync(key, cancellationToken);
        }

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Availability reconciliation must not create bytes.");

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Availability reconciliation only persists a cleanup obligation.");
    }

    private sealed class AvailabilityCommitFault(string connectionString, Guid uploadId, bool afterCommit) : DbTransactionInterceptor
    {
        internal bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && !Fired && await HasUnavailableAsync(Assert.IsType<NpgsqlConnection>(transaction.Connection),
                Assert.IsType<NpgsqlTransaction>(transaction), cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic failure before availability commit.");
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
            if (await HasUnavailableAsync(connection, null, cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic lost availability commit response.");
            }
        }

        private async Task<bool> HasUnavailableAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("select exists(select 1 from media.uploads where id=$1 and state='Rejected' and rejection_code='PHOTO_REVIEW_UNAVAILABLE')", connection, transaction);
            command.Parameters.AddWithValue(uploadId);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
    }

    private sealed class RefuseAvailabilityAudit : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into ops.audit_events", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is "safety.photo_review_unavailable"))
            {
                Fired = true;
                throw new IOException("Synthetic availability audit failure.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
