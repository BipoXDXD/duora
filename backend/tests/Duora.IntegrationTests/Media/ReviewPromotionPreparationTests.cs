using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Media;

public sealed partial class MediaPipelineTests
{
    [Fact]
    public async Task PromotionConfirmsItsOwnIntentAndManifestBeforeAnyPutWithoutPublishing()
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var originals = await CandidateBytesAsync(media.Store, await PromotionUploadAsync(request.CaseId));
        var store = new PromotionPreparationStore(media.Store, async key =>
        {
            Assert.Equal(StorageArea.Derivatives, key.Area);
            Assert.StartsWith("derivatives/review-", key.Value, StringComparison.Ordinal);
            Assert.Equal(1, await PromotionCountAsync("""
                select count(*) from media.review_promotions p
                where p.case_id=$1 and p.committed_at is null and p.writer_settled_at is null
                  and p.cleanup_requested_at is null and p.prepare_deadline>p.created_at
                  and p.case_version=$3 and p.upload_version=$4 and p.candidate_generation=$5 and p.policy_version=$6
                  and exists(select 1 from media.review_promotion_variants v
                    where v.generation=p.generation and v.object_key=$2
                      and v.object_key='derivatives/review-' || replace(p.generation::text,'-','') || '-' || v.kind)
                  and p.prepare_deadline<=p.created_at+interval '15 seconds'
                  and (select count(*) from media.review_promotion_variants v where v.generation=p.generation)=3
                """, request.CaseId, key.Value, request.CaseVersion, request.UploadVersion, request.CandidateGeneration, request.PolicyVersion));
        });

        var prepared = Assert.IsType<ReviewPromotionResult.Prepared>(await PreparePromotionAsync(request, store));

        Assert.Equal(3, store.Written.Count);
        Assert.Collection(store.Opened.Select(key => key.Value).Order(),
            key => Assert.Equal($"review-candidates/{request.CandidateGeneration:N}-card", key),
            key => Assert.Equal($"review-candidates/{request.CandidateGeneration:N}-full", key),
            key => Assert.Equal($"review-candidates/{request.CandidateGeneration:N}-thumb", key));
        Assert.Collection(store.Written.Select(key => key.Value.Split('-')[^1]).Order(),
            kind => Assert.Equal("card", kind), kind => Assert.Equal("full", kind), kind => Assert.Equal("thumb", kind));
        foreach (var kind in PhotoVariants.All)
        {
            var key = ObjectKey.Parse($"derivatives/review-{prepared.Generation:N}-{PhotoVariants.Name(kind)}");
            Assert.Contains(key, store.Written);
            await using var stream = await media.Store.OpenReadAsync(key, Cancellation);
            Assert.NotNull(stream);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Cancellation);
            Assert.Equal(originals[ObjectKey.ForReviewCandidate(request.CandidateGeneration, kind)], copy.ToArray());
        }

        Assert.Equal(1, await PromotionCountAsync("""
            select count(*) from media.review_promotions where generation=$1 and writer_settled_at is not null
              and committed_at is null and asset_id is null and cleanup_requested_at is null
            """, prepared.Generation));
        Assert.Equal(1, await PromotionCountAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview'", await PromotionUploadAsync(request.CaseId)));
        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.media_assets where upload_id=$1", await PromotionUploadAsync(request.CaseId)));
        await AssertPromotionCandidateUnchangedAsync(media.Store, originals);
    }

    [Theory]
    [InlineData("case-version")]
    [InlineData("upload-version")]
    [InlineData("generation")]
    [InlineData("policy")]
    [InlineData("case-missing")]
    public async Task PromotionRejectsAMismatchedSnapshotBeforeReadingOrWritingStorage(string mismatch)
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        request = mismatch switch
        {
            "case-version" => request with { CaseVersion = request.CaseVersion + 1 },
            "upload-version" => request with { UploadVersion = request.UploadVersion + 1 },
            "generation" => request with { CandidateGeneration = Guid.NewGuid() },
            "policy" => request with { PolicyVersion = "not-the-current-policy" },
            _ => request with { CaseId = Guid.NewGuid() },
        };
        var store = new PromotionPreparationStore(media.Store);

        Assert.IsType<ReviewPromotionResult.Unavailable>(await PreparePromotionAsync(request, store));

        Assert.Equal(0, store.Reads);
        Assert.Empty(store.Written);
        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.review_promotions"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("hash")]
    public async Task UnavailableCandidateBytesNeverProduceAPublishableResult(string failure)
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var original = await CandidateBytesAsync(media.Store, await PromotionUploadAsync(request.CaseId));
        var card = ObjectKey.ForReviewCandidate(request.CandidateGeneration, PhotoVariant.Card);
        byte[]? replacement = failure switch
        {
            "missing" => null,
            "short" => original[card][..^1],
            "long" => [.. original[card], 0],
            _ => original[card].ToArray(),
        };
        if (failure == "hash")
        {
            replacement![^1] ^= 1;
            Assert.Equal(PhotoFormat.WebP, PhotoFormats.Sniff(replacement));
            Assert.NotEqual(SHA256.HashData(original[card]), SHA256.HashData(replacement));
        }

        var store = new PromotionPreparationStore(media.Store, substitute: (card, replacement));

        Assert.IsType<ReviewPromotionResult.Unavailable>(await PreparePromotionAsync(request, store));

        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.media_assets where upload_id=$1", await PromotionUploadAsync(request.CaseId)));
        Assert.Equal(1, await PromotionCountAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview'", await PromotionUploadAsync(request.CaseId)));
        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.review_promotions where committed_at is not null"));
        await AssertPromotionCandidateUnchangedAsync(media.Store, original);
    }

    [Theory]
    [InlineData(PhotoVariant.Thumb)]
    [InlineData(PhotoVariant.Card)]
    [InlineData(PhotoVariant.Full)]
    public async Task PromotionVerifiesTheHashOfEveryDistinctVariant(PhotoVariant corrupted)
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var original = await CandidateBytesAsync(media.Store, await PromotionUploadAsync(request.CaseId));
        var key = ObjectKey.ForReviewCandidate(request.CandidateGeneration, corrupted);
        var altered = original[key].ToArray();
        altered[^1] ^= 1;
        Assert.Equal(PhotoFormat.WebP, PhotoFormats.Sniff(altered));
        Assert.NotEqual(SHA256.HashData(original[key]), SHA256.HashData(altered));
        var store = new PromotionPreparationStore(media.Store, substitute: (key, altered));

        Assert.IsType<ReviewPromotionResult.Unavailable>(await PreparePromotionAsync(request, store));

        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.media_assets where upload_id=$1", await PromotionUploadAsync(request.CaseId)));
        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.review_promotions where committed_at is not null"));
        await AssertPromotionCandidateUnchangedAsync(media.Store, original);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AFailedPromotionPutRetainsItsOwnDurableCleanupWithoutTouchingCandidate(int failingPut)
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var original = await CandidateBytesAsync(media.Store, await PromotionUploadAsync(request.CaseId));
        var store = new PromotionPreparationStore(media.Store, failingPut: failingPut);

        Assert.IsType<ReviewPromotionResult.Unavailable>(await PreparePromotionAsync(request, store));

        Assert.Equal(failingPut, store.PutAttempts);
        Assert.Equal(1, await PromotionCountAsync("""
            select count(*) from media.review_promotions p where p.case_id=$1 and p.committed_at is null
              and p.writer_settled_at is null and p.cleanup_requested_at is not null
              and (select count(*) from media.review_promotion_variants v where v.generation=p.generation)=3
            """, request.CaseId));
        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.media_assets where upload_id=$1", await PromotionUploadAsync(request.CaseId)));
        await AssertPromotionCandidateUnchangedAsync(media.Store, original);
    }

    [Fact]
    public async Task CandidateLocksAreReleasedDuringPromotionStorageIo()
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var changed = false;
        var store = new PromotionPreparationStore(media.Store, async _ =>
        {
            if (changed)
            {
                return;
            }

            await using var connection = await OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(Cancellation);
            await using (var timeout = new NpgsqlCommand("set local lock_timeout='250ms'", connection, transaction))
            {
                await timeout.ExecuteNonQueryAsync(Cancellation);
            }

            await using (var command = new NpgsqlCommand("update media.review_candidates set cleanup_requested_at=$2 where generation=$1", connection, transaction))
            {
                command.Parameters.AddWithValue(request.CandidateGeneration);
                command.Parameters.AddWithValue(clock.GetUtcNow());
                Assert.Equal(1, await command.ExecuteNonQueryAsync(Cancellation));
            }

            await transaction.CommitAsync(Cancellation);
            changed = true;
        });

        Assert.IsType<ReviewPromotionResult.Unavailable>(await PreparePromotionAsync(request, store, clock));

        Assert.True(changed);
        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.review_promotions where committed_at is not null"));
        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.media_assets where upload_id=$1", await PromotionUploadAsync(request.CaseId)));
    }

    [Fact]
    public async Task PromotionRefusesACallersTransactionBeforeAnyStorageIo()
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var store = new PromotionPreparationStore(media.Store);
        await using var scope = Web.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Cancellation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PhotoReviewPromotions(db, store, TimeProvider.System).PrepareAsync(request, Cancellation));

        Assert.Empty(store.Opened);
        Assert.Empty(store.Written);
        Assert.Equal(0, store.DeleteAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainPreparationCommitNeverIssuesAPut(bool afterCommit)
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var store = new PromotionPreparationStore(media.Store);
        var source = Web.Services.GetRequiredService<NpgsqlDataSource>();
        var fault = new PromotionPreparationCommitFault(source.ConnectionString, request.CaseId, afterCommit);

        Assert.IsType<ReviewPromotionResult.Unavailable>(await PreparePromotionAsync(request, store, interceptor: fault));

        Assert.True(fault.Fired);
        Assert.Equal(0, store.PutAttempts);
        Assert.Empty(store.Opened);
        Assert.Empty(store.Written);
        Assert.Equal(afterCommit ? 1 : 0, await PromotionCountAsync("select count(*) from media.review_promotions where case_id=$1", request.CaseId));
        Assert.Equal(afterCommit ? 3 : 0, await PromotionCountAsync("""
            select count(*) from media.review_promotion_variants v join media.review_promotions p using(generation)
            where p.case_id=$1
            """, request.CaseId));
        Assert.Equal(afterCommit ? 1 : 0, await PromotionCountAsync("""
            select count(*) from media.review_promotions where case_id=$1
              and writer_settled_at=created_at and committed_at is null
            """, request.CaseId));
        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.media_assets where upload_id=$1", await PromotionUploadAsync(request.CaseId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConcurrentCleanupClaimOrAckPreventsTheFirstPut(bool cleanupAcknowledged)
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var original = await CandidateBytesAsync(media.Store, await PromotionUploadAsync(request.CaseId));
        var store = new PromotionPreparationStore(media.Store);
        var source = Web.Services.GetRequiredService<NpgsqlDataSource>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fault = new CleanupBeforePromotionWriterBarrier(source.ConnectionString, request.CaseId, cleanupAcknowledged, clock);
        var started = Stopwatch.GetTimestamp();

        Assert.IsType<ReviewPromotionResult.Unavailable>(await PreparePromotionAsync(request, store, clock, fault));

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(14), "Cleanup refusal must precede the real 15-second budget.");
        Assert.True(fault.Fired);
        Assert.Equal(1, await PromotionCountAsync("select count(*) from media.review_promotions where case_id=$1 and prepare_deadline>$2",
            request.CaseId, clock.GetUtcNow()));
        Assert.Equal(0, store.PutAttempts);
        Assert.Empty(store.Written);
        Assert.Equal(0, await PromotionCountAsync("select count(*) from media.review_promotions where committed_at is not null"));
        await AssertPromotionCandidateUnchangedAsync(media.Store, original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AManifestLockWaitCannotExtendCandidateOrReviewDeadlines(bool reviewExpires)
    {
        var (media, request) = await PromotionReadyCandidateAsync();
        var source = Web.Services.GetRequiredService<NpgsqlDataSource>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        if (reviewExpires)
        {
            await using var connection = await OpenAsync();
            await using var deadline = new NpgsqlCommand("select review_deadline from safety.photo_review_cases where id=$1", connection);
            deadline.Parameters.AddWithValue(request.CaseId);
            await using var reader = await deadline.ExecuteReaderAsync(Cancellation);
            Assert.True(await reader.ReadAsync(Cancellation));
            clock.SetUtcNow(reader.GetFieldValue<DateTimeOffset>(0).AddSeconds(-1));
        }
        else
        {
            await using var connection = await OpenAsync();
            await using var retention = new NpgsqlCommand("update media.review_candidates set retention_until=$2 where generation=$1", connection);
            retention.Parameters.AddWithValue(request.CandidateGeneration);
            retention.Parameters.AddWithValue(clock.GetUtcNow().AddSeconds(1));
            Assert.Equal(1, await retention.ExecuteNonQueryAsync(Cancellation));
        }

        var name = $"promotion-deadline-{Guid.NewGuid():N}";
        var settings = new NpgsqlConnectionStringBuilder(source.ConnectionString) { ApplicationName = name };
        var options = new DbContextOptionsBuilder<DuoraDbContext>().UseNpgsql(settings.ConnectionString).Options;
        await using var injected = new DuoraDbContext(options);
        var store = new PromotionPreparationStore(media.Store);
        await using var held = await OpenAsync();
        await using var transaction = await held.BeginTransactionAsync(Cancellation);
        await using (var command = new NpgsqlCommand("""
            select 1 from media.review_candidate_variants where generation=$1 and kind='card' for update
            """, held, transaction))
        {
            command.Parameters.AddWithValue(request.CandidateGeneration);
            await command.ExecuteNonQueryAsync(Cancellation);
        }

        var started = Stopwatch.GetTimestamp();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var preparation = new PhotoReviewPromotions(injected, store, clock).PrepareAsync(request, attempt.Token);
        var released = false;
        try
        {
            await WaitForPromotionLockAsync(name);
            clock.Advance(TimeSpan.FromSeconds(2));
            await transaction.CommitAsync(Cancellation);
            released = true;
            Assert.IsType<ReviewPromotionResult.Unavailable>(await preparation.WaitAsync(TimeSpan.FromSeconds(5), Cancellation));
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(14), "Candidate expiry must precede the preparation budget.");
            Assert.Empty(store.Opened);
            Assert.Equal(0, store.PutAttempts);
            Assert.Equal(0, store.DeleteAttempts);
            Assert.Equal(0, await PromotionCountAsync("select count(*) from media.review_promotions where case_id=$1", request.CaseId));
            var activeClock = clock.GetUtcNow();
            Assert.Equal(1, await PromotionCountAsync(reviewExpires
                ? "select count(*) from safety.photo_review_cases r join media.review_candidates c on c.generation=r.candidate_generation where r.id=$1 and r.review_deadline<=$2 and c.retention_until>$2"
                : "select count(*) from safety.photo_review_cases r join media.review_candidates c on c.generation=r.candidate_generation where r.id=$1 and c.retention_until<=$2 and r.review_deadline>$2",
                request.CaseId, activeClock));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!released)
            {
                await transaction.RollbackAsync(cleanup.Token);
            }

            await attempt.CancelAsync();
            try
            {
                await preparation.WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (preparation.IsCompleted && attempt.IsCancellationRequested)
            {
                // The cancelled operation has finished and has been observed before its context is disposed.
            }
        }
    }

    private async Task WaitForPromotionLockAsync(string applicationName)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            if (await PromotionCountAsync("select count(*) from pg_stat_activity where application_name=$1 and wait_event_type='Lock'", applicationName) == 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), Cancellation);
        }

        Assert.Fail("Promotion never reached the controlled manifest lock.");
    }

    private async Task<(MediaScenario Media, ReviewPromotionRequest Request)> PromotionReadyCandidateAsync()
    {
        var (media, upload, version) = await ReviewReadyUploadAsync();
        var staged = Assert.IsType<ReviewCandidateStageResult.Staged>(await StageCandidateAsync(
            upload.Id, version, media.Store, DateTimeOffset.UtcNow.AddMinutes(1)));
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select candidate_generation,version,policy_version from safety.photo_review_cases where id=$1", connection);
        command.Parameters.AddWithValue(staged.CaseId);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        Assert.True(await reader.ReadAsync(Cancellation));
        return (media, new ReviewPromotionRequest(staged.CaseId, reader.GetInt64(1), version + 1, reader.GetGuid(0), reader.GetString(2)));
    }

    private async Task<long> PromotionCountAsync(string sql, params object[] values)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }

        return (long)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    private async Task<Guid> PromotionUploadAsync(Guid caseId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select upload_id from safety.photo_review_cases where id=$1", connection);
        command.Parameters.AddWithValue(caseId);
        return (Guid)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    private async Task<ReviewPromotionResult> PreparePromotionAsync(
        ReviewPromotionRequest request, PromotionPreparationStore store, TimeProvider? time = null, IInterceptor? interceptor = null)
    {
        await using var scope = Web.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
        if (interceptor is null)
        {
            var outcome = await new PhotoReviewPromotions(db, store, time ?? TimeProvider.System).PrepareAsync(request, Cancellation);
            Assert.Equal(0, store.DeleteAttempts);
            return outcome;
        }

        var options = new DbContextOptionsBuilder<DuoraDbContext>().UseNpgsql(db.Database.GetConnectionString())
            .AddInterceptors(interceptor).Options;
        await using var injected = new DuoraDbContext(options);
        var result = await new PhotoReviewPromotions(injected, store, time ?? TimeProvider.System).PrepareAsync(request, Cancellation);
        Assert.Equal(0, store.DeleteAttempts);
        return result;
    }

    private static async Task AssertPromotionCandidateUnchangedAsync(FileSystemObjectStore store, Dictionary<ObjectKey, byte[]> expected)
    {
        foreach (var (key, bytes) in expected)
        {
            await using var current = await store.OpenReadAsync(key, Cancellation);
            Assert.NotNull(current);
            Assert.Equal(SHA256.HashData(bytes), await SHA256.HashDataAsync(current, Cancellation));
        }
    }

    private sealed class PromotionPreparationStore(
        IObjectStore inner, Func<ObjectKey, Task>? beforePut = null, int? failingPut = null,
        (ObjectKey Key, byte[]? Bytes)? substitute = null) : IObjectStore
    {
        internal List<ObjectKey> Written { get; } = [];

        internal List<ObjectKey> Opened { get; } = [];

        internal int Reads { get; private set; }

        internal int PutAttempts { get; private set; }

        internal int DeleteAttempts { get; private set; }

        public async Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            PutAttempts++;
            if (beforePut is not null)
            {
                await beforePut(key);
            }

            if (PutAttempts == failingPut)
            {
                throw new IOException("Synthetic uncertain promotion PUT");
            }

            await inner.PutAsync(key, content, length, contentType, cancellationToken);
            Written.Add(key);
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Reads++;
            Opened.Add(key);
            return substitute is { } alternate && alternate.Key == key
                ? Task.FromResult<Stream?>(alternate.Bytes is null ? null : new MemoryStream(alternate.Bytes, writable: false))
                : inner.OpenReadAsync(key, cancellationToken);
        }

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            DeleteAttempts++;
            throw new InvalidOperationException("Preparation never deletes any asset or candidate.");
        }
    }

    private sealed class PromotionPreparationCommitFault(string connectionString, Guid caseId, bool afterCommit) : DbTransactionInterceptor
    {
        internal bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && !Fired && await HasPromotionAsync(Assert.IsType<NpgsqlConnection>(transaction.Connection),
                Assert.IsType<NpgsqlTransaction>(transaction), cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic failure before preparation commit");
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
            if (await HasPromotionAsync(connection, null, cancellationToken))
            {
                Fired = true;
                throw new IOException("Synthetic lost preparation commit response");
            }
        }

        private async Task<bool> HasPromotionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("select exists(select 1 from media.review_promotions where case_id=$1)", connection, transaction);
            command.Parameters.AddWithValue(caseId);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }
    }

    private sealed class CleanupBeforePromotionWriterBarrier(
        string connectionString, Guid caseId, bool acknowledged, TimeProvider time) : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var compact = command.CommandText.Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
            if (Fired || !compact.Contains("updatemedia.review_promotions", StringComparison.OrdinalIgnoreCase)
                || !compact.Contains("writer_settled_at=null", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            var now = time.GetUtcNow();
            await using (var claim = new NpgsqlCommand("""
                update media.review_promotions set cleanup_requested_at=$2,cleanup_token=$3,cleanup_lease_until=$4
                where case_id=$1 and committed_at is null and writer_settled_at is not null
                  and cleanup_requested_at is null and prepare_deadline>$2
                """, connection))
            {
                claim.Parameters.AddWithValue(caseId);
                claim.Parameters.AddWithValue(now);
                claim.Parameters.AddWithValue(Guid.NewGuid());
                claim.Parameters.AddWithValue(now.AddMinutes(1));
                Assert.Equal(1, await claim.ExecuteNonQueryAsync(cancellationToken));
            }

            if (acknowledged)
            {
                await using var ack = new NpgsqlCommand("""
                    update media.review_promotions set last_cleanup_at=$2,deleted_at=$2,cleanup_token=null,cleanup_lease_until=null
                    where case_id=$1 and writer_settled_at is not null and committed_at is null
                    """, connection);
                ack.Parameters.AddWithValue(caseId);
                ack.Parameters.AddWithValue(now);
                Assert.Equal(1, await ack.ExecuteNonQueryAsync(cancellationToken));
            }

            Fired = true;
            return result;
        }
    }
}
