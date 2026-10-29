using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Media;

public sealed partial class MediaPipelineTests
{
    [Fact]
    public async Task SanitizedCandidateCommitsReviewCaseAndOriginalCleanupWithoutAnApprovedAsset()
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var store = new OriginalDeletionRefusingStore(media.Store);

        var result = Assert.IsType<ReviewCandidateStageResult.Staged>(await StageCandidateAsync(
            upload.Id, version, store, DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Equal(1, await CountAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview'", upload.Id));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from safety.photo_review_cases where id=$1 and policy_version='human-review-v1'
              and review_deadline=awaiting_review_at+interval '168 hours' and version=1
            """, result.CaseId));
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates c join safety.photo_review_cases r
              on r.id=c.case_id and r.upload_id=c.upload_id and r.candidate_generation=c.generation
            where c.upload_id=$1 and c.committed_at is not null and c.writer_settled_at is not null
              and c.cleanup_requested_at is null
            """, upload.Id));
        Assert.Equal(3, await CountAsync("""
            select count(*) from media.review_candidate_variants v join media.review_candidates c using(generation)
            where c.upload_id=$1 and octet_length(v.sha256)=32
            """, upload.Id));
        Assert.Equal(3, (await CandidateBytesAsync(media.Store, upload.Id)).Count);
        Assert.Equal(1, await CountAsync("""
            select count(*) from ops.audit_events where target_id=$1 and target_type='photo_review_case'
              and action='safety.photo_review_requested' and actor_kind='System' and target_version=1
            """, result.CaseId));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Equal(1, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1", upload.Id));
        Assert.Equal("Pending", await DeliveryStatusAsync(upload.Id));
    }

    [Fact]
    public async Task ConfirmedStageReplayReturnsTheSameCaseWithoutNewWritesOrAudit()
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var store = new CandidateWriteCountingStore(media.Store);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        var first = Assert.IsType<ReviewCandidateStageResult.Staged>(await StageCandidateAsync(upload.Id, version, store, deadline));
        Assert.Equal(3, store.Writes);

        var replay = Assert.IsType<ReviewCandidateStageResult.Staged>(await StageCandidateAsync(upload.Id, version, store, deadline));

        Assert.Equal(first, replay);
        Assert.Equal(3, store.Writes);
        Assert.Equal(1, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_requested'
            """, first.CaseId));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
        Assert.Null(await media.ReadQuarantineAsync(upload));
    }

    [Theory]
    [InlineData("review-expired")]
    [InlineData("retention-expired")]
    [InlineData("missing-variant")]
    [InlineData("corrupt-variant")]
    public async Task ReplayRefusesExpiredOrUnavailableCandidatesWithoutRewritingHistory(string condition)
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var store = new CandidateWriteCountingStore(media.Store);
        var first = Assert.IsType<ReviewCandidateStageResult.Staged>(await StageCandidateAsync(
            upload.Id, version, store, DateTimeOffset.UtcNow.AddMinutes(1)));
        var bytes = await CandidateBytesAsync(media.Store, upload.Id);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow + (condition == "review-expired" ? TimeSpan.FromDays(7) + TimeSpan.FromHours(1) : TimeSpan.FromHours(1)));
        if (condition == "retention-expired")
        {
            await media.ExecuteAsync("update media.review_candidates set retention_until=$2 where upload_id=$1",
                upload.Id, clock.GetUtcNow().AddMinutes(-1));
        }
        else if (condition == "missing-variant")
        {
            await media.Store.DeleteAsync(bytes.Keys.First(), Cancellation);
        }
        else if (condition == "corrupt-variant")
        {
            using var content = new MemoryStream([0]);
            await media.Store.PutAsync(bytes.Keys.First(), content, content.Length, "image/webp", Cancellation);
        }

        Assert.IsType<ReviewCandidateStageResult.LeaseLost>(await StageCandidateAsync(
            upload.Id, version, store, clock.GetUtcNow().AddMinutes(1), clock));

        Assert.Equal(3, store.Writes);
        Assert.Equal(1, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_requested'
            """, first.CaseId));
        Assert.Equal(1, await CountAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview'", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is not null
              and cleanup_requested_at is null and deleted_at is null
            """, upload.Id));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AFailedCandidatePutKeepsDurableCleanupAndNeverCreatesACase(int failingPut)
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var store = new CandidateWriteCountingStore(media.Store, failingPut);

        await Assert.ThrowsAsync<IOException>(() => StageCandidateAsync(upload.Id, version, store, DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Equal(0, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
        Assert.Equal(1, await CountAsync("select count(*) from media.uploads where id=$1 and state='Processing'", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is null
              and writer_settled_at is null and cleanup_requested_at is not null
            """, upload.Id));
        Assert.Equal(3, await CountAsync("""
            select count(*) from media.review_candidate_variants v join media.review_candidates c using(generation)
            where c.upload_id=$1
            """, upload.Id));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
        Assert.Equal(0, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1", upload.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupRetainsTheObligationForALatePutAndOnlyASettledSweepMayFinish(bool settlesDuringSweep)
    {
        var (winnerMedia, winnerBrowser) = await SignInAsync();
        using var browserLifetime = winnerBrowser;
        var winnerId = await UploadAsync(winnerBrowser, TestImages.Jpeg(), "image/jpeg");
        var winnerUpload = Assert.Single(await winnerMedia.UploadsOfAsync(await OwnerOfAsync(winnerId)));
        Assert.Equal(1, await DispatchOnceAsync());
        var winnerBytes = new Dictionary<PhotoVariant, byte[]>();
        foreach (var variant in PhotoVariants.All)
        {
            winnerBytes[variant] = await ReadDerivativeAsync(winnerMedia, winnerUpload.QuarantineKey, variant);
        }

        var (privateMedia, privateUpload, privateVersion) = await ReviewReadyUploadAsync();
        Assert.IsType<ReviewCandidateStageResult.Staged>(await StageCandidateAsync(
            privateUpload.Id, privateVersion, privateMedia.Store, DateTimeOffset.UtcNow.AddMinutes(1)));
        var confirmedBytes = await CandidateBytesAsync(privateMedia.Store, privateUpload.Id);
        Assert.Equal(3, confirmedBytes.Count);

        var (media, upload, version) = await ReviewReadyUploadAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var store = new LateCandidateStore(media.Store);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var staging = StageCandidateAsync(upload.Id, version, store, clock.GetUtcNow().AddMinutes(1), clock, attempt.Token);
        IHost? recovered = null;
        try
        {
            await store.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
            clock.Advance(TimeSpan.FromMinutes(2));
            recovered = StartWorker(services =>
            {
                services.AddSingleton<IObjectStore>(store);
                services.AddSingleton<TimeProvider>(clock);
            }, "--Media:Review=");
            await recovered.StartAsync(Cancellation);
            await store.FullDeleted.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
            if (settlesDuringSweep)
            {
                store.AllowPut.TrySetResult();
                Assert.IsType<ReviewCandidateStageResult.LeaseLost>(await staging);
                Assert.Equal(1, await CountAsync("""
                    select count(*) from media.review_candidates where upload_id=$1 and writer_settled_at is not null
                    """, upload.Id));
            }

            store.AllowSweep.TrySetResult();
            await WaitForCandidateCountAsync("""
                select count(*) from media.review_candidates where upload_id=$1 and last_cleanup_at is not null
                """, upload.Id, 1);
            if (!settlesDuringSweep)
            {
                store.AllowPut.TrySetResult();
                Assert.IsType<ReviewCandidateStageResult.LeaseLost>(await staging);
            }

            Assert.Equal(1, await CountAsync("""
                select count(*) from media.review_candidates where upload_id=$1 and deleted_at is null
                  and cleanup_requested_at is not null and writer_settled_at is not null
                """, upload.Id));
            await using (var lateBytes = await media.Store.OpenReadAsync(store.FullKey, Cancellation))
            {
                Assert.NotNull(lateBytes);
            }

            clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
            await WaitForCandidateCountAsync("""
                select count(*) from media.review_candidates where upload_id=$1 and deleted_at is not null
                """, upload.Id, 1);
            foreach (var variant in PhotoVariants.All)
            {
                var key = ObjectKey.Parse(store.FullKey.Value.Replace("-full", "-" + PhotoVariants.Name(variant), StringComparison.Ordinal));
                await using var missing = await media.Store.OpenReadAsync(key, Cancellation);
                Assert.Null(missing);
            }

            Assert.Equal(0, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
            Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
            Assert.NotNull(await media.ReadQuarantineAsync(upload));
            Assert.Equal(("Approved", null), await StatusAsync(winnerBrowser, winnerId));
            foreach (var (variant, bytes) in winnerBytes)
            {
                Assert.Equal(bytes, await ReadDerivativeAsync(winnerMedia, winnerUpload.QuarantineKey, variant));
            }

            var stillConfirmed = await CandidateBytesAsync(privateMedia.Store, privateUpload.Id);
            Assert.Equal(3, stillConfirmed.Count);
            foreach (var (key, bytes) in confirmedBytes)
            {
                Assert.Equal(bytes, stillConfirmed[key]);
            }

            Assert.Equal(1, await CountAsync("""
                select count(*) from media.review_candidates where upload_id=$1 and committed_at is not null
                  and cleanup_requested_at is null and deleted_at is null
                """, privateUpload.Id));
        }
        finally
        {
            store.AllowPut.TrySetResult();
            store.AllowSweep.TrySetResult();
            await attempt.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await staging.WaitAsync(TimeSpan.FromSeconds(10), cleanup.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested && staging.IsCompleted)
            {
                // Observe the deliberately cancelled operation before disposing its database/storage fixture.
            }
            finally
            {
                if (recovered is not null)
                {
                    await recovered.StopAsync(cleanup.Token);
                }
            }
        }
    }

    [Theory]
    [InlineData("ops.audit_events")]
    [InlineData("media.original_cleanup")]
    public async Task ReviewCaseAuditAndOriginalCleanupRollBackTogetherOnDatabaseFailure(string failingTable)
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var migrator = new NpgsqlConnectionStringBuilder(postgres.MigratorConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(_connectionString).Database,
        };
        await using var connection = new NpgsqlConnection(migrator.ConnectionString);
        await connection.OpenAsync(Cancellation);
        // The table is a closed theory value; fault injection uses only this test database's schema owner.
        await using var install = new NpgsqlCommand($"""
            create function public.fail_review_commit() returns trigger language plpgsql as $$
            begin raise exception 'Synthetic review commit failure'; end $$;
            create trigger fail_review_commit before insert on {failingTable}
            for each row execute function public.fail_review_commit();
            """, connection);
        await install.ExecuteNonQueryAsync(Cancellation);

        await Assert.ThrowsAsync<PostgresException>(() => StageCandidateAsync(
            upload.Id, version, media.Store, DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Equal(1, await CountAsync("select count(*) from media.uploads where id=$1 and state='Processing'", upload.Id));
        Assert.Equal(0, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
        Assert.Equal(0, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1", upload.Id));
        Assert.Equal(0, await CountAsync("""
            select count(*) from ops.audit_events where action='safety.photo_review_requested' and $1::uuid is not null
            """, upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is null
              and case_id is null and writer_settled_at is not null and cleanup_requested_at is not null
            """, upload.Id));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
    }

    [Fact]
    public async Task LostResponseAfterCommitCannotAbandonTheConfirmedGenerationAndReplayIsSafe()
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var store = new CandidateWriteCountingStore(media.Store);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        var options = new DbContextOptionsBuilder<DuoraDbContext>().UseNpgsql(_connectionString)
            .AddInterceptors(new LoseReviewCommitResponse(_connectionString, upload.Id)).Options;
        await using (var db = new DuoraDbContext(options))
        {
            var stage = new PhotoReviewCandidates(db, store, TimeProvider.System, new MediaProcessingOptions());
            await Assert.ThrowsAsync<IOException>(() => stage.StageAsync(
                upload.Id, version, deadline, ReviewSanitized(), Cancellation));
        }

        Assert.Equal(1, await CountAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview'", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is not null
              and cleanup_requested_at is null
            """, upload.Id));
        Assert.Equal(3, store.Writes);
        var confirmedBytes = await CandidateBytesAsync(media.Store, upload.Id);
        var replay = Assert.IsType<ReviewCandidateStageResult.Staged>(await StageCandidateAsync(upload.Id, version, store, deadline));
        Assert.Equal(3, store.Writes);
        Assert.Equal(1, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from ops.audit_events where target_id=$1 and action='safety.photo_review_requested'
            """, replay.CaseId));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
        var replayBytes = await CandidateBytesAsync(media.Store, upload.Id);
        Assert.Equal(3, replayBytes.Count);
        foreach (var (key, bytes) in confirmedBytes)
        {
            Assert.Equal(bytes, replayBytes[key]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountRevokedDuringCandidateWritesPreventsTheReviewCommitWithoutHoldingStorageLocks(bool restrict)
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var store = new LateCandidateStore(media.Store);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var staging = StageCandidateAsync(upload.Id, version, store, DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken: attempt.Token);
        try
        {
            await store.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
            await using var connection = await OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(Cancellation);
            await using var setup = new NpgsqlCommand("set local lock_timeout='200ms'", connection, transaction);
            await setup.ExecuteNonQueryAsync(Cancellation);
            var sql = restrict
                ? "update identity.users set status='Restricted' where id=$1"
                : "update identity.users set email_confirmed=false where id=$1";
            await using var revoke = new NpgsqlCommand(sql, connection, transaction);
            revoke.Parameters.AddWithValue(await OwnerOfAsync(upload.Id));
            await revoke.ExecuteNonQueryAsync(Cancellation);
            await transaction.CommitAsync(Cancellation);
            store.AllowPut.TrySetResult();
            Assert.IsType<ReviewCandidateStageResult.LeaseLost>(await staging.WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
        }
        finally
        {
            store.AllowPut.TrySetResult();
            await attempt.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await staging.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested && staging.IsCompleted)
            {
                // Observe cancellation independently of the xUnit token, including failed setup paths.
            }
        }

        Assert.IsType<ReviewCandidateStageResult.LeaseLost>(await staging);
        Assert.Equal(0, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(0, await CountAsync("select count(*) from media.media_assets where upload_id=$1", upload.Id));
        Assert.Equal(0, await CountAsync("select count(*) from media.original_cleanup where upload_id=$1", upload.Id));
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is null
              and writer_settled_at is not null and cleanup_requested_at is not null
            """, upload.Id));
        Assert.NotNull(await media.ReadQuarantineAsync(upload));
    }

    private async Task WaitForCandidateCountAsync(string sql, Guid uploadId, long expected)
    {
        var waiting = Stopwatch.StartNew();
        while (await CountAsync(sql, uploadId) != expected && waiting.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Cancellation);
        }

        Assert.Equal(expected, await CountAsync(sql, uploadId));
    }

    [Fact]
    public async Task CandidateCleanupOwnershipCannotAcknowledgeOrRetryAfterItsLeaseWasReclaimed()
    {
        var (media, uploadId, _) = await AbandonedCandidateAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var cleanup = CandidateCleanup(media.Store, clock);
        var first = Assert.IsType<ReviewCandidateCleanupClaim>(await cleanup.ClaimAsync(Cancellation));
        Assert.False(first.WriterWasSettled);
        Assert.Null(await cleanup.ClaimAsync(Cancellation));
        clock.Advance(ReviewCandidateCleanupDispatcher.LeaseDuration);
        Assert.False(await cleanup.CompleteSweepAsync(first, Cancellation));
        Assert.False(await cleanup.RetryAsync(first, Cancellation));
        var next = Assert.IsType<ReviewCandidateCleanupClaim>(await cleanup.ClaimAsync(Cancellation));
        Assert.NotEqual(first.Token, next.Token);
        Assert.False(await cleanup.CompleteSweepAsync(first, Cancellation));
        Assert.False(await cleanup.RetryAsync(first, Cancellation));
        foreach (var kind in PhotoVariants.All)
        {
            await media.Store.DeleteAsync(ObjectKey.ForReviewCandidate(next.Generation, kind), Cancellation);
        }

        Assert.True(await cleanup.CompleteSweepAsync(next, Cancellation));
        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and deleted_at is null
              and last_cleanup_at is not null and cleanup_lease_until is null
            """, uploadId));
    }

    [Fact]
    public async Task ConcurrentCandidateCollectorsOwnAtMostOneClaim()
    {
        var (media, _, _) = await AbandonedCandidateAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var claims = await Task.WhenAll(CandidateCleanup(media.Store, clock).ClaimAsync(Cancellation),
            CandidateCleanup(media.Store, clock).ClaimAsync(Cancellation));

        Assert.Single(claims, claim => claim is not null);
    }

    [Fact]
    public async Task PartialCandidateDeletionRetriesAndNeverExpiresAnUncertainWriterObligation()
    {
        var (media, uploadId, generation) = await AbandonedCandidateAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(0, await CandidateCleanup(media.Store, clock, enabled: false).DispatchOnceAsync(Cancellation));
        var partial = CandidateCleanup(new PartialCandidateDeletionStore(media.Store), clock);

        Assert.Equal(1, await partial.DispatchOnceAsync(Cancellation));

        await using (var missing = await media.Store.OpenReadAsync(ObjectKey.ForReviewCandidate(generation, PhotoVariant.Thumb), Cancellation))
        {
            Assert.Null(missing);
        }

        await using (var remaining = await media.Store.OpenReadAsync(ObjectKey.ForReviewCandidate(generation, PhotoVariant.Card), Cancellation))
        {
            Assert.NotNull(remaining);
        }

        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and cleanup_attempts=1
              and cleanup_lease_until is null and deleted_at is null
            """, uploadId));
        var recovered = CandidateCleanup(media.Store, clock);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            clock.Advance(TimeSpan.FromMinutes(15));
            Assert.Equal(1, await recovered.DispatchOnceAsync(Cancellation));
        }

        foreach (var kind in PhotoVariants.All)
        {
            await using var missing = await media.Store.OpenReadAsync(ObjectKey.ForReviewCandidate(generation, kind), Cancellation);
            Assert.Null(missing);
        }

        Assert.Equal(1, await CountAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and cleanup_attempts=13
              and cleanup_requested_at is not null and writer_settled_at is null and deleted_at is null
            """, uploadId));
        Assert.Equal(0, await CountAsync("select count(*) from safety.photo_review_cases where upload_id=$1", uploadId));
    }

    [Fact]
    public async Task CandidateCleanupShutdownLeavesOwnershipForRecovery()
    {
        var (media, uploadId, generation) = await AbandonedCandidateAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var blocked = new BlockingOriginalStore(media.Store);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var pending = CandidateCleanup(blocked, clock).DispatchOnceAsync(shutdown.Token);
        try
        {
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
            await shutdown.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(1, await CountAsync("""
                select count(*) from media.review_candidates where upload_id=$1 and cleanup_lease_until is not null
                  and deleted_at is null
                """, uploadId));
            Assert.Null(await CandidateCleanup(media.Store, clock).ClaimAsync(Cancellation));
            clock.Advance(ReviewCandidateCleanupDispatcher.LeaseDuration);
            Assert.Equal(1, await CandidateCleanup(media.Store, clock).DispatchOnceAsync(Cancellation));
            foreach (var kind in PhotoVariants.All)
            {
                await using var missing = await media.Store.OpenReadAsync(ObjectKey.ForReviewCandidate(generation, kind), Cancellation);
                Assert.Null(missing);
            }
        }
        finally
        {
            blocked.Release.TrySetResult();
            await shutdown.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await pending.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (pending.IsCompleted)
            {
                // The cancelled DELETE has been observed before the fixture is disposed.
            }
        }
    }

    private ReviewCandidateCleanupDispatcher CandidateCleanup(IObjectStore store, TimeProvider clock, bool enabled = true) => new(
        Web.Services.GetRequiredService<NpgsqlDataSource>(), store, clock, enabled,
        NullLogger<ReviewCandidateCleanupDispatcher>.Instance);

    private async Task<(MediaScenario Media, Guid UploadId, Guid Generation)> AbandonedCandidateAsync()
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        await Assert.ThrowsAsync<IOException>(() => StageCandidateAsync(upload.Id, version,
            new CandidateWriteCountingStore(media.Store, failingPut: 2), DateTimeOffset.UtcNow.AddMinutes(1)));
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select generation from media.review_candidates where upload_id=$1", connection);
        command.Parameters.AddWithValue(upload.Id);
        var generation = (Guid)(await command.ExecuteScalarAsync(Cancellation))!;
        // Model uncertain writes reaching storage after a failed request. Cleanup must remove arbitrary partial bytes.
        foreach (var kind in PhotoVariants.All)
        {
            using var content = new MemoryStream([1, 2, 3]);
            await media.Store.PutAsync(ObjectKey.ForReviewCandidate(generation, kind), content, content.Length, "image/webp", Cancellation);
        }

        return (media, upload.Id, generation);
    }

    private async Task<(MediaScenario Media, StoredUpload Upload, long Version)> ReviewReadyUploadAsync()
    {
        var (media, browser) = await SignInAsync();
        using var browserLifetime = browser;
        var id = await UploadAsync(browser, TestImages.Jpeg(64, 32), "image/jpeg");
        var upload = Assert.Single(await media.UploadsOfAsync(await OwnerOfAsync(id)));
        await ExecuteAsync("update media.uploads set state='Processing',version=version+1 where id=$1", id);
        await using var scope = Web.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
        var version = await db.Set<Upload>().Where(row => row.Id == id).Select(row => row.Version).SingleAsync(Cancellation);
        return (media, upload, version);
    }

    private async Task<ReviewCandidateStageResult> StageCandidateAsync(
        Guid uploadId, long version, IObjectStore store, DateTimeOffset deadline, TimeProvider? time = null,
        CancellationToken? cancellationToken = null)
    {
        await using var scope = Web.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
        var stage = new PhotoReviewCandidates(db, store, time ?? TimeProvider.System, new MediaProcessingOptions());
        return await stage.StageAsync(uploadId, version, deadline, ReviewSanitized(), cancellationToken ?? Cancellation);
    }

    private async Task<Dictionary<ObjectKey, byte[]>> CandidateBytesAsync(FileSystemObjectStore store, Guid uploadId)
    {
        var metadata = new List<(ObjectKey Key, byte[] Sha256, long Size)>();
        await using (var connection = await OpenAsync())
        {
            await using var command = new NpgsqlCommand("""
                select v.object_key,v.sha256,v.size_bytes from media.review_candidate_variants v
                join media.review_candidates c using(generation) where c.upload_id=$1 order by v.kind
                """, connection);
            command.Parameters.AddWithValue(uploadId);
            await using var reader = await command.ExecuteReaderAsync(Cancellation);
            while (await reader.ReadAsync(Cancellation))
            {
                metadata.Add((ObjectKey.Parse(reader.GetString(0)), (byte[])reader[1], reader.GetInt64(2)));
            }
        }

        var result = new Dictionary<ObjectKey, byte[]>();
        foreach (var (key, hash, size) in metadata)
        {
            Assert.Equal(StorageArea.ReviewCandidates, key.Area);
            await using var stream = await store.OpenReadAsync(key, Cancellation);
            Assert.NotNull(stream);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Cancellation);
            var bytes = copy.ToArray();
            Assert.Equal(size, bytes.LongLength);
            Assert.Equal(hash, SHA256.HashData(bytes));
            Assert.Equal(PhotoFormat.WebP, PhotoFormats.Sniff(bytes));
            result.Add(key, bytes);
        }

        return result;
    }

    private static SanitizeOutcome.Sanitized ReviewSanitized() => new(PhotoFormat.Jpeg, 64, 32,
        [.. PhotoVariants.All.Select(kind => new SanitizedVariant(kind, TestImages.WebP(64, 32), 64, 32))]);

    private sealed class LoseReviewCommitResponse(string connectionString, Guid uploadId) : DbTransactionInterceptor
    {
        private bool _fired;

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (_fired)
            {
                return;
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                select exists(select 1 from safety.photo_review_cases where upload_id=$1)
                """, connection);
            command.Parameters.AddWithValue(uploadId);
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                _fired = true;
                throw new IOException("Synthetic lost response after review commit");
            }
        }
    }

    private sealed class CandidateWriteCountingStore(IObjectStore inner, int? failingPut = null) : IObjectStore
    {
        internal int Writes { get; private set; }

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            if (key.Area == StorageArea.ReviewCandidates && ++Writes == failingPut)
            {
                throw new IOException("Synthetic candidate storage failure");
            }

            return inner.PutAsync(key, content, length, contentType, cancellationToken);
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
    }

    private sealed class LateCandidateStore(IObjectStore inner) : IObjectStore
    {
        private int _fullDeletes;
        internal TaskCompletionSource PutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowPut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FullDeleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowSweep { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ObjectKey FullKey { get; private set; }

        public async Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            if (key.Area == StorageArea.ReviewCandidates && key.Value.EndsWith("-full", StringComparison.Ordinal))
            {
                FullKey = key;
                PutEntered.TrySetResult();
                // Simulate the storage completing a request after the caller's cancellation/deadline.
                await AllowPut.Task;
                await inner.PutAsync(key, content, length, contentType, CancellationToken.None);
                return;
            }

            await inner.PutAsync(key, content, length, contentType, cancellationToken);
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public async Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            await inner.DeleteAsync(key, cancellationToken);
            if (key.Area == StorageArea.ReviewCandidates && key.Value.EndsWith("-full", StringComparison.Ordinal)
                && Interlocked.Increment(ref _fullDeletes) == 1)
            {
                FullDeleted.TrySetResult();
                await AllowSweep.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class PartialCandidateDeletionStore(IObjectStore inner) : IObjectStore
    {
        private int _deletes;

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, contentType, cancellationToken);

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            if (key.Area == StorageArea.ReviewCandidates && Interlocked.Increment(ref _deletes) == 2)
            {
                throw new IOException("Synthetic partial candidate deletion");
            }

            return inner.DeleteAsync(key, cancellationToken);
        }
    }
}
