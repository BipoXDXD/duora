using System.Data.Common;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Characterization of <c>PhotoReviewCandidates.StageAsync</c> branches that the happy-path, replay and fault-injection
/// tests of <c>MediaPipelineTests</c> (Linux-only) and of the Safety suites do not reach. Each test records what the code
/// does today (result, storage traffic and durable rows) so a structural change cannot alter it unnoticed.
/// Runs on every OS: it needs only PostgreSQL and a file-system object store, no isolated Worker.
/// </summary>
public sealed class ReviewCandidateStageCharacterizationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("duora-review-stage-");
    private string _connectionString = "";
    private ScratchDatabase? _database;

    /// <summary>Smallest step PostgreSQL stores (1 µs); <c>prepare_deadline</c> must exceed <c>created_at</c> by at least this.</summary>
    private static readonly TimeSpan PostgresTick = TimeSpan.FromTicks(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        _connectionString = _database.AppConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        finally
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagingInsideACallersTransactionIsRefusedBeforeAnyReplayStorageOrDatabaseWork(bool alreadyStaged)
    {
        var upload = await ProcessingUploadAsync();
        var store = new WriteCountingStore(new FileSystemObjectStore(_root.FullName));
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        if (alreadyStaged)
        {
            Assert.IsType<ReviewCandidateStageResult.Staged>(
                await StageAsync(upload.Id, upload.Version, store, deadline, TimeProvider.System, ReviewSanitized()));
        }

        var writesBefore = store.Writes;
        await using (var db = Context())
        {
            await using var callersTransaction = await db.Database.BeginTransactionAsync(Ct);
            var stage = new PhotoReviewCandidates(db, store, TimeProvider.System, new MediaProcessingOptions());

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => stage.StageAsync(
                upload.Id, upload.Version, deadline, ReviewSanitized(), Ct));

            Assert.Equal("Storage preparation cannot run inside a caller's transaction.", refused.Message);
        }

        Assert.Equal(writesBefore, store.Writes);
        Assert.Equal(alreadyStaged ? 1 : 0, await CandidateRowsAsync(upload.Id));
        Assert.Equal(1, await ScalarAsync(
            $"select count(*) from media.uploads where id=$1 and state='{(alreadyStaged ? "AwaitingReview" : "Processing")}'",
            upload.Id));
    }

    [Fact]
    public async Task AnAlreadyCancelledStageThrowsBeforeTouchingStorageOrCreatingACandidate()
    {
        var upload = await ProcessingUploadAsync();
        var store = new WriteCountingStore(new FileSystemObjectStore(_root.FullName));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => StageAsync(
            upload.Id, upload.Version, store, DateTimeOffset.UtcNow.AddMinutes(1), TimeProvider.System, ReviewSanitized(),
            cancelled.Token));

        Assert.Equal(0, store.Writes);
        Assert.Equal(0, await CandidateRowsAsync(upload.Id));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Processing'", upload.Id));
    }

    [Theory]
    [InlineData("version-zero")]
    [InlineData("version-max")]
    [InlineData("lease-ends-now")]
    [InlineData("lease-ended-before-now")]
    [InlineData("lease-longer-than-the-processing-lease")]
    public async Task AnUnusableVersionOrLeaseWindowLosesTheLeaseWithoutStorageTrafficOrACandidate(string window)
    {
        var upload = await ProcessingUploadAsync();
        var store = new WriteCountingStore(new FileSystemObjectStore(_root.FullName));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var (expectedVersion, leaseExpiresAt) = window switch
        {
            "version-zero" => (0L, now.AddMinutes(1)),
            "version-max" => (long.MaxValue, now.AddMinutes(1)),
            "lease-ends-now" => (upload.Version, now),
            "lease-ended-before-now" => (upload.Version, now.AddSeconds(-1)),
            "lease-longer-than-the-processing-lease" => (upload.Version,
                now + new MediaProcessingOptions().LeaseDuration + TimeSpan.FromTicks(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown lease window."),
        };

        var result = await StageAsync(upload.Id, expectedVersion, store, leaseExpiresAt, clock, ReviewSanitized());

        Assert.IsType<ReviewCandidateStageResult.LeaseLost>(result);
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, await CandidateRowsAsync(upload.Id));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Processing'", upload.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALeaseEndingOneMicrosecondAfterNowOrExactlyAtTheProcessingLeaseStillStages(bool atTheProcessingLease)
    {
        var upload = await ProcessingUploadAsync();
        var store = new WriteCountingStore(new FileSystemObjectStore(_root.FullName));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var leaseExpiresAt = atTheProcessingLease
            ? clock.GetUtcNow() + new MediaProcessingOptions().LeaseDuration
            : clock.GetUtcNow() + PostgresTick;

        var result = await StageAsync(upload.Id, upload.Version, store, leaseExpiresAt, clock, ReviewSanitized());

        Assert.IsType<ReviewCandidateStageResult.Staged>(result);
        Assert.Equal(3, store.Writes);
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview'", upload.Id));
    }

    [Theory]
    [InlineData("missing-upload")]
    [InlineData("older-version")]
    [InlineData("newer-version")]
    [InlineData("no-longer-processing")]
    [InlineData("different-detected-format")]
    public async Task AnUploadThatNoLongerMatchesTheLeaseLosesItWithoutStorageTrafficOrACandidate(string mismatch)
    {
        var upload = await ProcessingUploadAsync();
        var store = new WriteCountingStore(new FileSystemObjectStore(_root.FullName));
        var uploadId = upload.Id;
        var expectedVersion = upload.Version;
        var sanitized = ReviewSanitized();
        switch (mismatch)
        {
            case "missing-upload":
                uploadId = Guid.NewGuid();
                break;
            case "older-version":
                expectedVersion -= 1;
                break;
            case "newer-version":
                expectedVersion += 1;
                break;
            case "no-longer-processing":
                await ExecuteAsync("update media.uploads set state='Quarantined' where id=$1", upload.Id);
                break;
            case "different-detected-format":
                sanitized = sanitized with { Detected = PhotoFormat.Png };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mismatch), mismatch, "Unknown mismatch.");
        }

        var result = await StageAsync(
            uploadId, expectedVersion, store, DateTimeOffset.UtcNow.AddMinutes(1), TimeProvider.System, sanitized);

        Assert.IsType<ReviewCandidateStageResult.LeaseLost>(result);
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, await CandidateRowsAsync(upload.Id));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.original_cleanup where upload_id=$1", upload.Id));
        await AssertOriginalKeptAsync(upload);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnInvalidSanitizedShapeThrowsOnlyForAnUploadThatPassedTheLeaseChecks(bool uploadExists)
    {
        var upload = await ProcessingUploadAsync();
        var store = new WriteCountingStore(new FileSystemObjectStore(_root.FullName));
        var sanitized = ReviewSanitized();
        var twoVariantsOnly = sanitized with { Variants = [.. sanitized.Variants.Take(2)] };

        var stage = StageAsync(uploadExists ? upload.Id : Guid.NewGuid(), upload.Version, store,
            DateTimeOffset.UtcNow.AddMinutes(1), TimeProvider.System, twoVariantsOnly);

        if (uploadExists)
        {
            var invalid = await Assert.ThrowsAsync<InvalidDataException>(() => stage);
            Assert.Equal("Invalid sanitized candidate shape.", invalid.Message);
        }
        else
        {
            Assert.IsType<ReviewCandidateStageResult.LeaseLost>(await stage);
        }

        Assert.Equal(0, store.Writes);
        Assert.Equal(0, await CandidateRowsAsync(upload.Id));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Processing'", upload.Id));
    }

    [Fact]
    public async Task ASecondStageForAnAbandonedVersionLosesTheLeaseWithoutWritesOrRescheduledCleanup()
    {
        var upload = await ProcessingUploadAsync();
        var files = new FileSystemObjectStore(_root.FullName);
        await Assert.ThrowsAsync<IOException>(() => StageAsync(upload.Id, upload.Version,
            new WriteCountingStore(files, failingPut: 2), DateTimeOffset.UtcNow.AddMinutes(1), TimeProvider.System,
            ReviewSanitized()));
        var abandoned = await CandidateRowsSnapshotAsync(upload.Id);
        var store = new WriteCountingStore(files);

        var result = await StageAsync(
            upload.Id, upload.Version, store, DateTimeOffset.UtcNow.AddMinutes(1), TimeProvider.System, ReviewSanitized());

        Assert.IsType<ReviewCandidateStageResult.LeaseLost>(result);
        Assert.Equal(0, store.Writes);
        Assert.Equal(abandoned, await CandidateRowsSnapshotAsync(upload.Id));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Processing'", upload.Id));
    }

    [Fact]
    public async Task ACompetingStageThatCommitsDuringPreparationIsReturnedAsTheWinnerWithoutWritesFromTheLoser()
    {
        var upload = await ProcessingUploadAsync();
        var store = new WriteCountingStore(new FileSystemObjectStore(_root.FullName));
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        ReviewCandidateStageResult.Staged? competitor = null;
        var interceptor = new RunBeforeCandidateInsert(async () =>
        {
            competitor = Assert.IsType<ReviewCandidateStageResult.Staged>(
                await StageAsync(upload.Id, upload.Version, store, deadline, TimeProvider.System, ReviewSanitized()));
        });

        ReviewCandidateStageResult result;
        await using (var db = Context(interceptor))
        {
            result = await new PhotoReviewCandidates(db, store, TimeProvider.System, new MediaProcessingOptions())
                .StageAsync(upload.Id, upload.Version, deadline, ReviewSanitized(), Ct);
        }

        Assert.NotNull(competitor);
        Assert.Equal(competitor, Assert.IsType<ReviewCandidateStageResult.Staged>(result));
        Assert.Equal(3, store.Writes);
        Assert.Equal(1, await CandidateRowsAsync(upload.Id));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is not null
              and cleanup_requested_at is null
            """, upload.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationDuringAPutPropagatesInsteadOfLosingTheLeaseAndKeepsTheSettledCleanupObligation(
        bool leaseAlsoExpires)
    {
        var upload = await ProcessingUploadAsync();
        var store = new LatePutStore(new FileSystemObjectStore(_root.FullName));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var staging = StageAsync(upload.Id, upload.Version, store, clock.GetUtcNow().AddMinutes(1), clock,
            ReviewSanitized(), attempt.Token);
        try
        {
            await store.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            if (leaseAlsoExpires)
            {
                clock.Advance(TimeSpan.FromMinutes(2));
            }

            await attempt.CancelAsync();
            store.AllowPut.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staging.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        }
        finally
        {
            store.AllowPut.TrySetResult();
            await attempt.CancelAsync();
            try
            {
                await staging.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            }
            catch (OperationCanceledException)
            {
                // The cancellation is the behavior under test; observe it before disposing the database fixture.
            }
        }

        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.original_cleanup where upload_id=$1", upload.Id));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Processing'", upload.Id));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is null and case_id is null
              and writer_settled_at is not null and cleanup_requested_at is not null
            """, upload.Id));
        await AssertOriginalKeptAsync(upload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACommittedStageDeletesTheOriginalAndItsCleanupObligationUnlessTheDeletionFails(bool deletionFails)
    {
        var upload = await ProcessingUploadAsync();
        var store = new WriteCountingStore(new FileSystemObjectStore(_root.FullName), refuseQuarantineDeletion: deletionFails);

        var result = await StageAsync(
            upload.Id, upload.Version, store, DateTimeOffset.UtcNow.AddMinutes(1), TimeProvider.System, ReviewSanitized());

        Assert.IsType<ReviewCandidateStageResult.Staged>(result);
        Assert.Equal(deletionFails, await OriginalExistsAsync(upload));
        Assert.Equal(deletionFails ? 1 : 0, await ScalarAsync("select count(*) from media.original_cleanup where upload_id=$1", upload.Id));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='AwaitingReview'", upload.Id));
    }

    [Fact]
    public async Task AManagedStoreIsRegisteredThenGetsEveryVariantThroughItsOwnPutThenSealedBeforeTheCaseCommits()
    {
        var upload = await ProcessingUploadAsync();
        var store = new RecordingManagedStore(new FileSystemObjectStore(_root.FullName));

        var result = Assert.IsType<ReviewCandidateStageResult.Staged>(await StageAsync(
            upload.Id, upload.Version, store, DateTimeOffset.UtcNow.AddMinutes(1), TimeProvider.System, ReviewSanitized()));

        Assert.Equal("register,put:Thumb,put:Card,put:Full,seal", string.Join(',', store.Calls));
        Assert.Equal(3, await ScalarAsync("""
            select count(*) from media.review_candidate_variants v join media.review_candidates c using(generation)
            where c.upload_id=$1
            """, upload.Id));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.private_generation_bindings b join media.review_candidates c using(generation)
            where c.upload_id=$1
            """, upload.Id));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_cases where id=$1", result.CaseId));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is not null
              and case_id=$2 and cleanup_requested_at is null
            """, upload.Id, result.CaseId));
    }

    [Theory]
    [InlineData("register", true)]
    [InlineData("put", false)]
    [InlineData("seal", true)]
    public async Task AManagedStoreFailureKeepsCleanupAndMarksTheWriterSettledOnlyWhenNoPutWasInFlight(
        string failingCall, bool writerSettled)
    {
        var upload = await ProcessingUploadAsync();
        var store = new RecordingManagedStore(new FileSystemObjectStore(_root.FullName), failingCall);

        await Assert.ThrowsAsync<IOException>(() => StageAsync(
            upload.Id, upload.Version, store, DateTimeOffset.UtcNow.AddMinutes(1), TimeProvider.System, ReviewSanitized()));

        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_cases where upload_id=$1", upload.Id));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Processing'", upload.Id));
        Assert.Equal(1, await ScalarAsync($"""
            select count(*) from media.review_candidates where upload_id=$1 and committed_at is null
              and writer_settled_at is {(writerSettled ? "not null" : "null")} and cleanup_requested_at is not null
            """, upload.Id));
        await AssertOriginalKeptAsync(upload);
    }

    private DuoraDbContext Context(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(_connectionString).AddInterceptors(interceptors);
        return new DuoraDbContext(options.Options);
    }

    private async Task<ReviewCandidateStageResult> StageAsync(
        Guid uploadId, long expectedVersion, IObjectStore store, DateTimeOffset leaseExpiresAt, TimeProvider time,
        SanitizeOutcome.Sanitized sanitized, CancellationToken? cancellationToken = null)
    {
        await using var db = Context();
        var stage = new PhotoReviewCandidates(db, store, time, new MediaProcessingOptions());
        return await stage.StageAsync(uploadId, expectedVersion, leaseExpiresAt, sanitized, cancellationToken ?? Ct);
    }

    /// <summary>A confirmed owner and a Jpeg upload leased for processing (version 2) with its original in quarantine.</summary>
    private async Task<UploadSeed> ProcessingUploadAsync()
    {
        var owner = Guid.NewGuid();
        var original = TestImages.Jpeg(64, 32);
        var now = DateTimeOffset.UtcNow;
        Assert.True(IdempotencyKey.TryParse(Guid.NewGuid().ToString("N"), out var key));
        var upload = Upload.Reserve(owner, key, PhotoFormat.Jpeg, original.Length, now);
        await using (var db = Context())
        {
            await db.Database.ExecuteSqlAsync($"""
                insert into identity.users(id,status,created_at,email_confirmed,phone_number_confirmed,
                  two_factor_enabled,lockout_enabled,access_failed_count,security_stamp)
                values({owner},'Active',{now},true,false,true,true,0,'synthetic-stage-stamp')
                """, Ct);
            db.Add(upload);
            await db.SaveChangesAsync(Ct);
            var hash = SHA256.HashData(original);
            await db.Database.ExecuteSqlAsync($"""
                update media.uploads set state='Processing',version=version+1,content_sha256={hash} where id={upload.Id}
                """, Ct);
        }

        using var bytes = new MemoryStream(original);
        await new FileSystemObjectStore(_root.FullName).PutAsync(
            ObjectKey.Parse(upload.QuarantineKey), bytes, original.Length, "image/jpeg", Ct);
        return new UploadSeed(upload.Id, upload.Version + 1, upload.QuarantineKey);
    }

    private async Task AssertOriginalKeptAsync(UploadSeed upload) => Assert.True(await OriginalExistsAsync(upload));

    private async Task<bool> OriginalExistsAsync(UploadSeed upload)
    {
        await using var original = await new FileSystemObjectStore(_root.FullName)
            .OpenReadAsync(ObjectKey.Parse(upload.QuarantineKey), Ct);
        return original is not null;
    }

    private async Task<long> CandidateRowsAsync(Guid uploadId) =>
        await ScalarAsync("select count(*) from media.review_candidates where upload_id=$1", uploadId);

    /// <summary>Every column that cleanup scheduling or the writer ACK may rewrite, in a comparable form.</summary>
    private async Task<string> CandidateRowsSnapshotAsync(Guid uploadId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select coalesce(string_agg(concat_ws('|', generation, committed_at, writer_settled_at, cleanup_requested_at,
              cleanup_available_at, deleted_at), ',' order by generation), '') from media.review_candidates where upload_id=$1
            """, connection);
        command.Parameters.AddWithValue(uploadId);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> ScalarAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SanitizeOutcome.Sanitized ReviewSanitized() => new(PhotoFormat.Jpeg, 64, 32,
        [.. PhotoVariants.All.Select(kind => new SanitizedVariant(kind, TestImages.WebP(64, 32), 64, 32))]);

    private sealed record UploadSeed(Guid Id, long Version, string QuarantineKey);

    private sealed class RunBeforeCandidateInsert(Func<Task> beforeInsert) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("insert into media.review_candidates", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await beforeInsert();
            }

            return result;
        }
    }

    private sealed class WriteCountingStore(IObjectStore inner, int? failingPut = null, bool refuseQuarantineDeletion = false)
        : IObjectStore
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

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            refuseQuarantineDeletion && key.Area == StorageArea.Quarantine
                ? throw new IOException("Synthetic original deletion failure")
                : inner.DeleteAsync(key, cancellationToken);
    }

    /// <summary>Blocks the last variant's PUT until released, then completes it ignoring the caller's token.</summary>
    private sealed class LatePutStore(IObjectStore inner) : IObjectStore
    {
        internal TaskCompletionSource PutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowPut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            if (key.Area == StorageArea.ReviewCandidates && key.Value.EndsWith("-full", StringComparison.Ordinal))
            {
                PutEntered.TrySetResult();
                await AllowPut.Task;
                await inner.PutAsync(key, content, length, contentType, CancellationToken.None);
                return;
            }

            await inner.PutAsync(key, content, length, contentType, cancellationToken);
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
    }

    private sealed class RecordingManagedStore(IObjectStore inner, string? failingCall = null)
        : IObjectStore, IPrivateGenerationStore
    {
        public PrivateGenerationDomain Domain { get; } = new(Guid.NewGuid(), 1, 1);

        internal List<string> Calls { get; } = [];

        public Task RegisterAsync(PrivateGenerationManifest manifest, CancellationToken cancellationToken)
        {
            Record("register");
            return Task.CompletedTask;
        }

        public Task PutVariantAsync(PrivateGenerationManifest manifest, PhotoVariant kind, Stream content,
            CancellationToken cancellationToken)
        {
            Record("put:" + kind);
            return inner.PutAsync(manifest.Key(kind), content, manifest.Variants.Single(v => v.Kind == kind).Length,
                IPhotoSanitizer.VariantContentType, cancellationToken);
        }

        public Task SealAsync(PrivateGenerationManifest manifest, CancellationToken cancellationToken)
        {
            Record("seal");
            return Task.CompletedTask;
        }

        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
        {
            Assert.NotEqual(StorageArea.ReviewCandidates, key.Area);
            return inner.PutAsync(key, content, length, contentType, cancellationToken);
        }

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

        public Task<PrivateGenerationCloseReceipt> CloseAsync(PrivateGenerationIdentity identity, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Staging never closes a generation.");

        public Task<Guid> SweepAsync(PrivateGenerationCloseReceipt receipt, Guid cleanupToken, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Staging never sweeps a generation.");

        /// <summary>The failing "put" is the second variant, so the first one proves the writer was settled before it.</summary>
        private void Record(string call)
        {
            Calls.Add(call);
            var name = call.Split(':')[0];
            var isTheFailingCall = name == failingCall
                && (name != "put" || Calls.Count(c => c.StartsWith("put:", StringComparison.Ordinal)) == 2);
            if (isTheFailingCall)
            {
                throw new IOException("Synthetic managed storage failure");
            }
        }
    }
}
