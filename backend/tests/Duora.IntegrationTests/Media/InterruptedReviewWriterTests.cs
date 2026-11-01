using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace Duora.IntegrationTests.Media;

public sealed partial class ReviewPromotionCollectorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInterruptedFilesystemWriteCanReappearAfterRepeatedCleanupAndRecovery(bool promotion)
    {
        var (generation, table) = await SeedInterruptedWriterAsync(promotion);
        var store = new FileSystemObjectStore(_root.FullName);
        var key = PrivateKey(promotion, generation);
        await using var content = new PausedAtEndStream();
        // The storage operation has already accepted bytes. Losing its caller is not a storage fence.
        var writing = store.PutAsync(key, content, 3, "image/webp", Ct);
        try
        {
            await content.AtEnd.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Single(_root.EnumerateFiles("*.partial", SearchOption.AllDirectories));
            await AssertAbsentAsync(store, key);

            // A new collector instance after a very long pause still cannot infer that the writer died.
            _time.Advance(TimeSpan.FromDays(730));
            for (var sweep = 0; sweep < 3; sweep++)
            {
                Assert.Equal(1, await SweepPrivateAsync(promotion, store));
                await AssertUnsettledAsync(table, generation);
                await AssertAbsentAsync(store, key);
                _time.Advance(ReviewCandidateCleanupDispatcher.UncertainWriterPoll);
            }

            content.Resume.TrySetResult();
            await writing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await using (var reappeared = await store.OpenReadAsync(key, Ct))
            {
                Assert.NotNull(reappeared);
                using var bytes = new MemoryStream();
                await reappeared.CopyToAsync(bytes, Ct);
                Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());
            }

            // Simulate a lost settlement acknowledgement: successful storage I/O alone is not durable proof.
            Assert.Equal(1, await SweepPrivateAsync(promotion, store));
            await AssertAbsentAsync(store, key);
            await AssertUnsettledAsync(table, generation);
            Assert.Empty(_root.EnumerateFiles("*.partial", SearchOption.AllDirectories));

            // Only the original writer can now attest that all its I/O ended and it will issue no more PUTs.
            await ExecuteAsync($"update {table} set writer_settled_at=statement_timestamp() where generation=$1", generation);
            _time.Advance(ReviewCandidateCleanupDispatcher.UncertainWriterPoll);
            Assert.Equal(1, await SweepPrivateAsync(promotion, store));
            await AssertTerminalAsync(table, generation);
            Assert.Equal(0, await SweepPrivateAsync(promotion, store));
        }
        finally
        {
            content.Resume.TrySetResult();
            await writing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettlementDuringDeletionRequiresANewSweepBeforeTerminalCompletion(bool promotion)
    {
        var (generation, table) = await SeedInterruptedWriterAsync(promotion);
        var store = new FileSystemObjectStore(_root.FullName);
        var key = PrivateKey(promotion, generation);
        var barrier = new PausedAfterDeleteStore(store, key);
        var sweeping = SweepPrivateAsync(promotion, barrier);
        try
        {
            await barrier.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            // Last PUT returns after this key's DELETE. Settlement is valid, but this sweep missed its bytes.
            await PutAsync(store, key);
            await ExecuteAsync($"update {table} set writer_settled_at=statement_timestamp() where generation=$1", generation);
            barrier.Resume.TrySetResult();
            Assert.Equal(1, await sweeping.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.Equal(1, await CountAsync($"select count(*) from {table} where generation=$1 and deleted_at is null and writer_settled_at is not null", generation));
            await using (var late = await store.OpenReadAsync(key, Ct))
            {
                Assert.NotNull(late);
            }

            _time.Advance(ReviewCandidateCleanupDispatcher.UncertainWriterPoll);
            Assert.Equal(1, await SweepPrivateAsync(promotion, store));
            await AssertAbsentAsync(store, key);
            await AssertTerminalAsync(table, generation);
        }
        finally
        {
            barrier.Resume.TrySetResult();
            await sweeping.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryFencesTheOldCollectorButDoesNotSettleTheWriter(bool promotion)
    {
        var (generation, table) = await SeedInterruptedWriterAsync(promotion);
        var store = new FileSystemObjectStore(_root.FullName);
        if (promotion)
        {
            var first = Assert.IsType<ReviewPromotionCleanupClaim>(await Collector(store).ClaimAsync(Ct));
            Assert.Null(await Collector(store).ClaimAsync(Ct));
            _time.Advance(ReviewPromotionCleanupDispatcher.LeaseDuration);
            var next = Assert.IsType<ReviewPromotionCleanupClaim>(await Collector(store).ClaimAsync(Ct));
            Assert.NotEqual(first.Token, next.Token);
            Assert.False(await Collector(store).CompleteSweepAsync(first, Ct));
            Assert.False(await Collector(store).RetryAsync(first, Ct));
        }
        else
        {
            var first = Assert.IsType<ReviewCandidateCleanupClaim>(await CandidateCollector(store).ClaimAsync(Ct));
            Assert.Null(await CandidateCollector(store).ClaimAsync(Ct));
            _time.Advance(ReviewCandidateCleanupDispatcher.LeaseDuration);
            var next = Assert.IsType<ReviewCandidateCleanupClaim>(await CandidateCollector(store).ClaimAsync(Ct));
            Assert.NotEqual(first.Token, next.Token);
            Assert.False(await CandidateCollector(store).CompleteSweepAsync(first, Ct));
            Assert.False(await CandidateCollector(store).RetryAsync(first, Ct));
        }

        Assert.Equal(1, await CountAsync($"select count(*) from {table} where generation=$1 and cleanup_attempts=2 and cleanup_token is not null and writer_settled_at is null and deleted_at is null", generation));
        _time.Advance(ReviewCandidateCleanupDispatcher.LeaseDuration);
        Assert.Equal(1, await SweepPrivateAsync(promotion, store));
        await AssertUnsettledAsync(table, generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnsettledWriterHoldsAnOldTombstoneAndSettlementStillRequiresTheBackupWindow(bool promotion)
    {
        var (generation, table) = await SeedInterruptedWriterAsync(promotion);
        var store = new FileSystemObjectStore(_root.FullName);
        // Remove only the other synthetic preparation as an independent reason to retain this tombstone.
        var otherTable = promotion ? "media.review_candidates" : "media.review_promotions";
        await ExecuteAsync($"""
            update {otherTable} set cleanup_requested_at=created_at,cleanup_token=null,cleanup_lease_until=null,
              last_cleanup_at=statement_timestamp()-interval '66 days',deleted_at=statement_timestamp()-interval '66 days'
            """);
        await ExecuteAsync($"update media.uploads set state='Deleted' where id=(select upload_id from {table} where generation=$1)", generation);
        await ExecuteAsync($"""
            insert into media.photo_tombstones(photo_id,owner_id,operation_key,intent_sha256,deleted_version,deleted_at)
            select u.id,u.owner_id,encode(sha256(convert_to(u.id::text,'UTF8')),'hex'),
              sha256(convert_to(u.id::text,'UTF8')),u.version,statement_timestamp()-interval '2 years'
            from media.uploads u join {table} p on p.upload_id=u.id where p.generation=$1
            """, generation);
        Assert.Equal(1, await SweepPrivateAsync(promotion, store));
        await AssertUnsettledAsync(table, generation);
        await ExecuteAsync("select * from media.purge_photo_deletion_records('9999-01-01'::timestamptz,1000)");
        Assert.Equal(1, await CountAsync("select count(*) from media.photo_tombstones"));

        // Synthetic testimony of a writer which has finished: never inferred from a deadline or an empty key.
        await ExecuteAsync($"update {table} set writer_settled_at=statement_timestamp() where generation=$1", generation);
        _time.Advance(ReviewCandidateCleanupDispatcher.UncertainWriterPoll);
        Assert.Equal(1, await SweepPrivateAsync(promotion, store));
        await AssertTerminalAsync(table, generation);
        await ExecuteAsync("select * from media.purge_photo_deletion_records('9999-01-01'::timestamptz,1000)");
        Assert.Equal(1, await CountAsync("select count(*) from media.photo_tombstones"));
        await ExecuteAsync($"update {table} set deleted_at=statement_timestamp()-interval '65 days' where generation=$1", generation);
        await ExecuteAsync("select * from media.purge_photo_deletion_records('9999-01-01'::timestamptz,1000)");
        Assert.Equal(0, await CountAsync("select count(*) from media.photo_tombstones"));
        Assert.Equal(1, await CountAsync("select count(*) from media.uploads where state='Deleted'"));
    }

    private async Task<(Guid Generation, string Table)> SeedInterruptedWriterAsync(bool promotion)
    {
        var generation = await SeedAbandonedPromotionAsync();
        if (promotion)
        {
            await ExecuteAsync("update media.review_promotions set writer_settled_at=null where generation=$1", generation);
            return (generation, "media.review_promotions");
        }

        // The fixture's candidate is normally already deleted. Reopen only this synthetic preparation.
        await ExecuteAsync("""
            update media.review_candidates set writer_settled_at=null,deleted_at=null,last_cleanup_at=null
            where generation=(select candidate_generation from media.review_promotions where generation=$1)
            """, generation);
        await using var connection = await Source.OpenConnectionAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "select candidate_generation from media.review_promotions where generation=$1", connection);
        command.Parameters.AddWithValue(generation);
        return ((Guid)(await command.ExecuteScalarAsync(Ct))!, "media.review_candidates");
    }

    private ReviewCandidateCleanupDispatcher CandidateCollector(IObjectStore store) =>
        new(Source, store, _time, enabled: true, NullLogger<ReviewCandidateCleanupDispatcher>.Instance);

    private Task<int> SweepPrivateAsync(bool promotion, IObjectStore store) => promotion
        ? Collector(store).DispatchOnceAsync(Ct)
        : CandidateCollector(store).DispatchOnceAsync(Ct);

    private static ObjectKey PrivateKey(bool promotion, Guid generation) => promotion
        ? ObjectKey.ForPromotedDerivative(generation, PhotoVariant.Full)
        : ObjectKey.ForReviewCandidate(generation, PhotoVariant.Full);

    private Task AssertUnsettledAsync(string table, Guid generation) => AssertCountAsync($"""
        select count(*) from {table} where generation=$1 and writer_settled_at is null and deleted_at is null
          and cleanup_requested_at is not null and last_cleanup_at is not null and cleanup_token is null
        """, generation);

    private Task AssertTerminalAsync(string table, Guid generation) => AssertCountAsync($"""
        select count(*) from {table} where generation=$1 and writer_settled_at is not null
          and deleted_at=last_cleanup_at and deleted_at between statement_timestamp()-interval '1 minute' and statement_timestamp()
          and cleanup_token is null
        """, generation);

    private async Task AssertCountAsync(string sql, Guid generation) => Assert.Equal(1, await CountAsync(sql, generation));

    private static async Task AssertAbsentAsync(FileSystemObjectStore store, ObjectKey key)
    {
        await using var absent = await store.OpenReadAsync(key, Ct);
        Assert.Null(absent);
    }

    /// <summary>Pauses real filesystem I/O after all bytes were copied, before the final rename.</summary>
    private sealed class PausedAtEndStream : MemoryStream
    {
        internal TaskCompletionSource AtEnd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PausedAtEndStream() : base([1, 2, 3], writable: false)
        {
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length)
            {
                AtEnd.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class PausedAfterDeleteStore(IObjectStore inner, ObjectKey keyToPause) : IObjectStore
    {
        internal TaskCompletionSource Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, contentType, cancellationToken);

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public async Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            await inner.DeleteAsync(key, cancellationToken);
            if (key == keyToPause)
            {
                Deleted.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
