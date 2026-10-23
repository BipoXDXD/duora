using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Media;

public sealed partial class MediaPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromotionCleanupMustSweepAgainAfterALatePutEvenIfWriterSettlesDuringDeletion(bool settlesDuringSweep)
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var candidate = await CandidateBytesAsync(media.Store, await PromotionUploadAsync(request.CaseId));
        var sentinels = await PromotionCleanupSentinelsAsync(media.Store);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new LatePromotionStore(media.Store);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        using var sweeping = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        await using var scope = Web.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
        var preparation = new PhotoReviewPromotions(db, store, clock).PrepareAsync(request, attempt.Token);
        Task<int>? sweep = null;
        try
        {
            await store.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
            clock.Advance(TimeSpan.FromSeconds(16));
            sweep = PromotionCleanup(store, clock).DispatchOnceAsync(sweeping.Token);
            await store.FullDeleted.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
            if (settlesDuringSweep)
            {
                store.AllowPut.TrySetResult();
                Assert.IsType<ReviewPromotionResult.Unavailable>(await preparation.WaitAsync(TimeSpan.FromSeconds(5), Cancellation));
                Assert.Equal(1, await PromotionCountAsync("select count(*) from media.review_promotions where case_id=$1 and writer_settled_at is not null", request.CaseId));
            }

            store.AllowSweep.TrySetResult();
            Assert.Equal(1, await sweep.WaitAsync(TimeSpan.FromSeconds(5), Cancellation));
            if (!settlesDuringSweep)
            {
                store.AllowPut.TrySetResult();
                Assert.IsType<ReviewPromotionResult.Unavailable>(await preparation.WaitAsync(TimeSpan.FromSeconds(5), Cancellation));
            }

            Assert.Equal(1, await PromotionCountAsync("""
                select count(*) from media.review_promotions where case_id=$1 and deleted_at is null
                  and last_cleanup_at is not null and writer_settled_at is not null and cleanup_token is null
                  and cleanup_requested_at is not null and committed_at is null
                """, request.CaseId));
            await using (var late = await media.Store.OpenReadAsync(store.FullKey, Cancellation))
            {
                Assert.NotNull(late);
            }

            // A new dispatcher models process restart. Settlement during the first DELETE cannot ACK it.
            clock.Advance(ReviewPromotionCleanupDispatcher.UncertainWriterPoll.Add(TimeSpan.FromSeconds(1)));
            Assert.Equal(1, await PromotionCleanup(media.Store, clock).DispatchOnceAsync(Cancellation));
            Assert.Equal(1, await PromotionCountAsync("select count(*) from media.review_promotions where case_id=$1 and deleted_at is not null", request.CaseId));
            await AssertPromotionKeysMissingAsync(media.Store, await PromotionGenerationAsync(request.CaseId));
            await AssertPromotionCandidateUnchangedAsync(media.Store, candidate);
            await AssertPromotionCandidateUnchangedAsync(media.Store, sentinels);
            Assert.Equal(3, store.PutAttempts);
            Assert.Equal(0, await PromotionCountAsync("select count(*) from media.media_assets where upload_id=$1", await PromotionUploadAsync(request.CaseId)));
        }
        finally
        {
            store.AllowPut.TrySetResult();
            store.AllowSweep.TrySetResult();
            await attempt.CancelAsync();
            await sweeping.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task[] accepted = sweep is null ? [preparation] : [preparation, sweep];
            var observed = Task.WhenAll(accepted);
            try
            {
                await observed.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (observed.IsCompleted && observed.IsCanceled
                && attempt.IsCancellationRequested && sweeping.IsCancellationRequested)
            {
                // WhenAll observes both accepted operations even if either has an unexpected fault.
            }
        }
    }

    [Fact]
    public async Task PromotionCleanupOldOwnershipCannotAcknowledgeOrRetryAReplacementLease()
    {
        var (media, request, generation) = await AbandonedPromotionAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var cleanup = PromotionCleanup(media.Store, clock);
        var first = Assert.IsType<ReviewPromotionCleanupClaim>(await cleanup.ClaimAsync(Cancellation));
        Assert.Equal(generation, first.Generation);
        Assert.False(first.WriterWasSettled);
        Assert.Null(await cleanup.ClaimAsync(Cancellation));
        clock.Advance(ReviewPromotionCleanupDispatcher.LeaseDuration);
        Assert.False(await cleanup.CompleteSweepAsync(first, Cancellation));
        Assert.False(await cleanup.RetryAsync(first, Cancellation));
        var next = Assert.IsType<ReviewPromotionCleanupClaim>(await PromotionCleanup(media.Store, clock).ClaimAsync(Cancellation));
        Assert.NotEqual(first.Token, next.Token);
        Assert.False(await cleanup.CompleteSweepAsync(first, Cancellation));
        Assert.False(await cleanup.RetryAsync(first, Cancellation));
        Assert.Equal(1, await PromotionCountAsync("select count(*) from media.review_promotions where generation=$1 and cleanup_token=$2 and cleanup_attempts=2", generation, next.Token));
        foreach (var kind in PhotoVariants.All)
        {
            await media.Store.DeleteAsync(ObjectKey.ForPromotedDerivative(generation, kind), Cancellation);
        }

        Assert.True(await cleanup.CompleteSweepAsync(next, Cancellation));
        Assert.Equal(1, await PromotionCountAsync("""
            select count(*) from media.review_promotions where case_id=$1 and deleted_at is null
              and last_cleanup_at is not null and cleanup_token is null and writer_settled_at is null
            """, request.CaseId));
    }

    [Fact]
    public async Task ConcurrentPromotionCollectorsCannotOwnTheSameGeneration()
    {
        var (media, _, generation) = await AbandonedPromotionAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var claims = await Task.WhenAll(PromotionCleanup(media.Store, clock).ClaimAsync(Cancellation),
            PromotionCleanup(media.Store, clock).ClaimAsync(Cancellation));

        var winner = Assert.Single(claims, claim => claim is not null);
        Assert.Equal(generation, winner!.Generation);
    }

    [Fact]
    public async Task PromotionPartialDeletionRetriesAcrossRestartsWithoutInventingWriterSettlement()
    {
        var (media, _, generation) = await AbandonedPromotionAsync();
        var sentinels = await PromotionCleanupSentinelsAsync(media.Store);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var disabled = new PromotionDeleteStore(media.Store);
        Assert.Equal(0, await PromotionCleanup(disabled, clock, enabled: false).DispatchOnceAsync(Cancellation));
        Assert.Equal(0, disabled.Deletes);
        var partial = new PromotionDeleteStore(media.Store, failAt: 2);
        Assert.Equal(1, await PromotionCleanup(partial, clock).DispatchOnceAsync(Cancellation));
        Assert.Equal(2, partial.Deletes);
        await using (var removed = await media.Store.OpenReadAsync(ObjectKey.ForPromotedDerivative(generation, PhotoVariant.Thumb), Cancellation))
        {
            Assert.Null(removed);
        }

        await using (var stillPresent = await media.Store.OpenReadAsync(ObjectKey.ForPromotedDerivative(generation, PhotoVariant.Card), Cancellation))
        {
            Assert.NotNull(stillPresent);
        }

        Assert.Equal(0, await PromotionCleanup(media.Store, clock).DispatchOnceAsync(Cancellation));
        for (var attempt = 0; attempt < 12; attempt++)
        {
            clock.Advance(TimeSpan.FromMinutes(15));
            Assert.Equal(1, await PromotionCleanup(media.Store, clock).DispatchOnceAsync(Cancellation));
        }

        Assert.Equal(1, await PromotionCountAsync("""
            select count(*) from media.review_promotions where generation=$1 and cleanup_attempts=13
              and cleanup_requested_at is not null and writer_settled_at is null and deleted_at is null and cleanup_token is null
            """, generation));
        await AssertPromotionKeysMissingAsync(media.Store, generation);
        await AssertPromotionCandidateUnchangedAsync(media.Store, sentinels);
    }

    [Fact]
    public async Task PromotionCleanupShutdownKeepsItsClaimUntilARecoveryCanOwnIt()
    {
        var (media, _, generation) = await AbandonedPromotionAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var blocked = new BlockingOriginalStore(media.Store);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var pending = PromotionCleanup(blocked, clock).DispatchOnceAsync(shutdown.Token);
        try
        {
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
            await shutdown.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Cancellation));
            Assert.Equal(1, await PromotionCountAsync("select count(*) from media.review_promotions where generation=$1 and cleanup_token is not null and deleted_at is null", generation));
            Assert.Null(await PromotionCleanup(media.Store, clock).ClaimAsync(Cancellation));
            clock.Advance(ReviewPromotionCleanupDispatcher.LeaseDuration);
            Assert.Equal(1, await PromotionCleanup(media.Store, clock).DispatchOnceAsync(Cancellation));
            Assert.Equal(1, await PromotionCountAsync("select count(*) from media.review_promotions where generation=$1 and deleted_at is null and writer_settled_at is null", generation));
            await AssertPromotionKeysMissingAsync(media.Store, generation);
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
            catch (OperationCanceledException) when (pending.IsCompleted && shutdown.IsCancellationRequested)
            {
                // No unobserved DELETE survives teardown.
            }
        }
    }

    [Fact]
    public async Task AnUncertainInitialPromotionCommitWithNoPutCanBeCollectedAsSettled()
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new PromotionPreparationStore(media.Store);
        var fault = new PromotionPreparationCommitFault(_connectionString, request.CaseId, afterCommit: true);
        Assert.IsType<ReviewPromotionResult.Unavailable>(await PreparePromotionAsync(request, store, clock, fault));
        Assert.True(fault.Fired);
        Assert.Equal(0, store.PutAttempts);
        var generation = await PromotionGenerationAsync(request.CaseId);
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(1, await PromotionCleanup(media.Store, clock).DispatchOnceAsync(Cancellation));
        Assert.Equal(1, await PromotionCountAsync("select count(*) from media.review_promotions where generation=$1 and deleted_at is not null and writer_settled_at=created_at", generation));
        await AssertPromotionKeysMissingAsync(media.Store, generation);
    }

    [Fact]
    public async Task AnUnusedPreparedPromotionWaitsForItsDeadlineAndDeletesOnlyItsThreeKeys()
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var candidate = await CandidateBytesAsync(media.Store, await PromotionUploadAsync(request.CaseId));
        var sentinels = await PromotionCleanupSentinelsAsync(media.Store);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var prepared = Assert.IsType<ReviewPromotionResult.Prepared>(await PreparePromotionAsync(request, new PromotionPreparationStore(media.Store), clock));
        var deletions = new PromotionDeleteStore(media.Store);
        Assert.Equal(0, await PromotionCleanup(deletions, clock).DispatchOnceAsync(Cancellation));
        Assert.Equal(0, deletions.Deletes);
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(1, await PromotionCleanup(deletions, clock).DispatchOnceAsync(Cancellation));
        Assert.Equal(3, deletions.Deletes);
        Assert.Equal(0, await PromotionCleanup(deletions, clock).DispatchOnceAsync(Cancellation));
        Assert.Equal(1, await PromotionCountAsync("select count(*) from media.review_promotions where generation=$1 and deleted_at is not null", prepared.Generation));
        await AssertPromotionKeysMissingAsync(media.Store, prepared.Generation);
        await AssertPromotionCandidateUnchangedAsync(media.Store, candidate);
        await AssertPromotionCandidateUnchangedAsync(media.Store, sentinels);
    }

    [Fact]
    public async Task ACommittedPromotionAndItsExactBytesAreExcludedFromAbandonedCleanup()
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var prepared = Assert.IsType<ReviewPromotionResult.Prepared>(await PreparePromotionAsync(request, new PromotionPreparationStore(media.Store), clock));
        var bytes = await PromotionBytesAsync(media.Store, prepared.Generation);
        var asset = Guid.NewGuid();
        await using (var connection = await OpenAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Cancellation))
        {
            // Synthetic committed ownership only; this is not a human approval coordinator or an activation.
            await using var insert = new NpgsqlCommand("""
                insert into media.media_assets(id,upload_id,owner_id,state,detected_type,width,height,sha256,approved_at,version)
                select $2,p.upload_id,u.owner_id,'Approved',p.detected_type,p.width,p.height,$3,$4,1
                from media.review_promotions p join media.uploads u on u.id=p.upload_id where p.generation=$1
                """, connection, transaction);
            insert.Parameters.AddWithValue(prepared.Generation);
            insert.Parameters.AddWithValue(asset);
            insert.Parameters.AddWithValue(new byte[32]);
            insert.Parameters.AddWithValue(clock.GetUtcNow());
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(Cancellation));
            await using var commit = new NpgsqlCommand("update media.review_promotions set committed_at=$2,asset_id=$3 where generation=$1", connection, transaction);
            commit.Parameters.AddWithValue(prepared.Generation);
            commit.Parameters.AddWithValue(clock.GetUtcNow());
            commit.Parameters.AddWithValue(asset);
            Assert.Equal(1, await commit.ExecuteNonQueryAsync(Cancellation));
            await transaction.CommitAsync(Cancellation);
        }

        clock.Advance(TimeSpan.FromDays(40));
        var deletions = new PromotionDeleteStore(media.Store);
        Assert.Null(await PromotionCleanup(deletions, clock).ClaimAsync(Cancellation));
        Assert.Equal(0, await PromotionCleanup(deletions, clock).DispatchOnceAsync(Cancellation));
        Assert.Equal(0, deletions.Deletes);
        await AssertPromotionCandidateUnchangedAsync(media.Store, bytes);
        Assert.Equal(1, await PromotionCountAsync("select count(*) from media.review_promotions where generation=$1 and committed_at is not null and cleanup_requested_at is null and deleted_at is null", prepared.Generation));
    }

    private ReviewPromotionCleanupDispatcher PromotionCleanup(IObjectStore store, TimeProvider clock, bool enabled = true) =>
        new(Web.Services.GetRequiredService<NpgsqlDataSource>(), store, clock, enabled);

    private async Task<(MediaScenario Media, ReviewPromotionRequest Request, Guid Generation)> AbandonedPromotionAsync()
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        Assert.IsType<ReviewPromotionResult.Unavailable>(await PreparePromotionAsync(request, new PromotionPreparationStore(media.Store, failingPut: 2)));
        var generation = await PromotionGenerationAsync(request.CaseId);
        foreach (var kind in PhotoVariants.All)
        {
            using var bytes = new MemoryStream([1, 2, 3]);
            await media.Store.PutAsync(ObjectKey.ForPromotedDerivative(generation, kind), bytes, bytes.Length, "image/webp", Cancellation);
        }

        return (media, request, generation);
    }

    private async Task<Guid> PromotionGenerationAsync(Guid caseId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select generation from media.review_promotions where case_id=$1", connection);
        command.Parameters.AddWithValue(caseId);
        return (Guid)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    private static async Task<Dictionary<ObjectKey, byte[]>> PromotionCleanupSentinelsAsync(FileSystemObjectStore store)
    {
        var keys = new[] { ObjectKey.ForDerivative(ObjectKey.NewQuarantine(), PhotoVariant.Card), ObjectKey.ForPromotedDerivative(Guid.NewGuid(), PhotoVariant.Full) };
        var sentinels = new Dictionary<ObjectKey, byte[]>();
        foreach (var key in keys)
        {
            byte[] bytes = [9, 7, 5, 3, 1];
            using var content = new MemoryStream(bytes);
            await store.PutAsync(key, content, content.Length, "image/webp", Cancellation);
            sentinels.Add(key, bytes);
        }

        return sentinels;
    }

    private static async Task<Dictionary<ObjectKey, byte[]>> PromotionBytesAsync(FileSystemObjectStore store, Guid generation)
    {
        var result = new Dictionary<ObjectKey, byte[]>();
        foreach (var kind in PhotoVariants.All)
        {
            var key = ObjectKey.ForPromotedDerivative(generation, kind);
            await using var stream = await store.OpenReadAsync(key, Cancellation);
            Assert.NotNull(stream);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, Cancellation);
            result.Add(key, buffer.ToArray());
        }

        return result;
    }

    private static async Task AssertPromotionKeysMissingAsync(FileSystemObjectStore store, Guid generation)
    {
        foreach (var kind in PhotoVariants.All)
        {
            await using var absent = await store.OpenReadAsync(ObjectKey.ForPromotedDerivative(generation, kind), Cancellation);
            Assert.Null(absent);
        }
    }

    private sealed class PromotionDeleteStore(IObjectStore inner, int failAt = 0) : IObjectStore
    {
        internal int Deletes { get; private set; }

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, contentType, cancellationToken);

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Deletes++;
            if (Deletes == failAt)
            {
                throw new IOException("Synthetic partial promotion deletion.");
            }

            return inner.DeleteAsync(key, cancellationToken);
        }
    }

    private sealed class LatePromotionStore(IObjectStore inner) : IObjectStore
    {
        private int _fullDeletes;
        internal int PutAttempts { get; private set; }
        internal ObjectKey FullKey { get; private set; }
        internal TaskCompletionSource PutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowPut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FullDeleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowSweep { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            PutAttempts++;
            if (key.Area == StorageArea.Derivatives && key.Value.EndsWith("-full", StringComparison.Ordinal))
            {
                FullKey = key;
                PutEntered.TrySetResult();
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
            if (key.Area == StorageArea.Derivatives && key.Value.EndsWith("-full", StringComparison.Ordinal)
                && Interlocked.Increment(ref _fullDeletes) == 1)
            {
                FullDeleted.TrySetResult();
                await AllowSweep.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
