using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Theory]
    [InlineData("expired")]
    [InlineData("unavailable")]
    [InlineData("decided")]
    public async Task ManagedCollectorsRequireTheirOwnCurrentSweepAndKeepHistoryAndUnrelatedBytes(string outcome)
    {
        _time.SetUtcNow(DateTimeOffset.UtcNow);
        await using var app = NpgsqlDataSource.Create(_connectionString);
        await using var verifier = NpgsqlDataSource.Create((_database
            ?? throw new InvalidOperationException("Missing fixture database.")).MigratorConnectionString);
        // Controlled authority adapter: tests real preparation/binding, collector SQL, ordering and proof ownership.
        // Kernel isolation, external revocation and fsync are covered by the separate native Linux process drill.
        var store = new ManagedCollectorStore(new FileSystemObjectStore(_root.FullName), app, verifier);
        CaseData item;
        if (outcome == "decided")
        {
            var prepared = await PreparedAsync(storage: store);
            item = prepared.Item;
            Assert.IsType<PhotoReviewApprovalResult.Approved>(
                await ApproveAsync(prepared.Reviewer, item, Key()));
        }
        else
        {
            item = await CaseAsync(storage: store);
            if (outcome == "expired")
            {
                _time.Advance(ReviewWait + TimeSpan.FromHours(1));
                Assert.Equal(1, await new PhotoReviewExpirations(Factory(), _time).ExpireOnceAsync(Ct));
            }
            else
            {
                await LoseCandidateVariantAsync(item, PhotoVariant.Full);
                Assert.Equal(1, await Availability().ReconcileAsync(item.CaseId, Ct));
            }
        }
        var generation = await CleanupGenerationAsync(item);
        var before = Files();
        var history = await CleanupHistoryAsync();
        var retention = await ScalarAsync("select (extract(epoch from retention_until)*1000000)::bigint from media.review_candidates where generation=$1", generation);
        var retained = before.Where(file => !file.StartsWith($"review-candidates/{generation:N}-", StringComparison.Ordinal)).ToArray();
        Task<int> Dispatch() => outcome switch
        {
            "expired" => ExpiredCleanup(app, store).DispatchOnceAsync(Ct),
            "unavailable" => UnavailableCleanup(app, store).DispatchOnceAsync(Ct),
            _ => ApprovedCleanup(app, store).DispatchOnceAsync(Ct),
        };
        // A valid binding does not bypass the existing obligation's due time.
        await ExecuteAsync("update media.review_candidates set cleanup_available_at=$2 where generation=$1",
            generation, _time.GetUtcNow().AddMinutes(1));
        Assert.Equal(0, await Dispatch());
        Assert.Empty(store.Calls);
        await ExecuteAsync("update media.review_candidates set cleanup_available_at=$2 where generation=$1",
            generation, _time.GetUtcNow());
        store.FailSweep = true;
        Assert.Equal(1, await Dispatch());
        Assert.Equal("close,sweep", string.Join(',', store.Calls));
        Assert.Equal(before, Files());
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where generation=$1 and deleted_at is null
              and storage_proof_id is null and cleanup_token is null
            """, generation));
        store.FailSweep = false;
        _time.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(1, await Dispatch());
        Assert.Equal("close,sweep,close,sweep", string.Join(',', store.Calls));
        Assert.Equal(retained, Files());
        Assert.Equal(history, await CleanupHistoryAsync());
        Assert.Equal(retention, await ScalarAsync("select (extract(epoch from retention_until)*1000000)::bigint from media.review_candidates where generation=$1", generation));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates c join media.private_generation_proofs p on p.id=c.storage_proof_id
            where c.generation=$1 and c.deleted_at is not null and c.committed_at is not null
              and p.generation=c.generation and p.cleanup_token=$2 and c.cleanup_token is null
            """, generation, store.LastToken));
        Assert.Equal(0, await Dispatch());
    }

    private sealed class ManagedCollectorStore(IObjectStore files, NpgsqlDataSource app, NpgsqlDataSource verifier)
        : IObjectStore, IPrivateGenerationStore
    {
        public PrivateGenerationDomain Domain { get; } = new(Guid.NewGuid(), 1, 1);
        internal List<string> Calls { get; } = [];
        internal bool FailSweep { get; set; }
        internal Guid LastToken { get; private set; }
        private PrivateGenerationIdentity? _registered;
        private PrivateGenerationCloseReceipt? _closed;

        public Task RegisterAsync(PrivateGenerationManifest manifest, CancellationToken cancellationToken)
        { manifest.Validate(); _registered = manifest.Identity; return Task.CompletedTask; }
        public Task SealAsync(PrivateGenerationManifest manifest, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PutVariantAsync(PrivateGenerationManifest manifest, PhotoVariant kind, Stream content,
            CancellationToken cancellationToken) => files.PutAsync(manifest.Key(kind), content,
                manifest.Variants.Single(v => v.Kind == kind).Length, "image/webp", cancellationToken);
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            Assert.False(PrivateGenerationManifest.TryPrivateKey(key, out _, out _, out _));
            return files.PutAsync(key, content, length, contentType, cancellationToken);
        }
        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => files.OpenReadAsync(key, cancellationToken);
        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A managed collector must never fall back to generic DELETE.");
        public async Task<PrivateGenerationCloseReceipt> CloseAsync(PrivateGenerationIdentity identity,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_registered, identity);
            await AssertNoRowLockAsync(identity.Generation, cancellationToken);
            Calls.Add("close");
            return _closed = new(identity, 7);
        }
        public async Task<Guid> SweepAsync(PrivateGenerationCloseReceipt receipt, Guid cleanupToken,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_closed, receipt);
            await AssertNoRowLockAsync(receipt.Identity.Generation, cancellationToken);
            Calls.Add("sweep");
            if (FailSweep)
            { throw new IOException("Synthetic incomplete sweep."); }
            LastToken = cleanupToken;
            foreach (var kind in PhotoVariants.All)
            { await files.DeleteAsync(ObjectKey.ForReviewCandidate(receipt.Identity.Generation, kind), cancellationToken); }
            return await new PrivateGenerationAuthorityMetadata(verifier).RecordSweepAsync(receipt, cleanupToken, cancellationToken);
        }
        private async Task AssertNoRowLockAsync(Guid generation, CancellationToken cancellationToken)
        {
            await using var connection = await app.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "select generation from media.review_candidates where generation=$1 for update nowait", connection, transaction);
            command.Parameters.AddWithValue(generation);
            Assert.Equal(generation, await command.ExecuteScalarAsync(cancellationToken));
            await transaction.RollbackAsync(cancellationToken);
        }
    }
}
