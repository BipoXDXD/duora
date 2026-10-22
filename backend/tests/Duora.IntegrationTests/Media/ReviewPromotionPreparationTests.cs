using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
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
        ReviewPromotionRequest request, PromotionPreparationStore store, TimeProvider? time = null)
    {
        await using var scope = Web.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DuoraDbContext>();
        return await new PhotoReviewPromotions(db, store, time ?? TimeProvider.System).PrepareAsync(request, Cancellation);
    }

    private async Task AssertPromotionCandidateUnchangedAsync(FileSystemObjectStore store, Dictionary<ObjectKey, byte[]> expected)
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

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preparation never deletes any asset or candidate.");
    }
}
