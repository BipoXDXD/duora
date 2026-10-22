using System.Net;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>M1: leitores antes dos escritores; estados sintéticos são inseridos só pelo teste.</summary>
[Trait("Acceptance", "T-16")]
public sealed class MediaReviewCompatibilityTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = "";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData("AwaitingReview", null)]
    [InlineData("Rejected", "PHOTO_REVIEW_REJECTED")]
    [InlineData("Rejected", "PHOTO_REVIEW_EXPIRED")]
    [InlineData("Rejected", "PHOTO_REVIEW_UNAVAILABLE")]
    public async Task RestartedReadersPreserveStatusReplayAndOwnerIsolation(string state, string? code)
    {
        var bytes = MediaScenario.JpegLike();
        var key = MediaScenario.NewKey();
        TestAccount account;
        Guid photoId;
        await using (var writer = new DuoraWebFactory(_connectionString))
        {
            var media = new MediaScenario(_connectionString, writer);
            account = await media.Identity.CreateConfirmedAccountAsync();
            using var browser = await media.Identity.SignedInBrowserAsync(account);
            using var created = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            photoId = Assert.Single(await media.UploadsOfAsync(account.UserId)).Id;
            await media.ExecuteAsync("update media.uploads set state = $2, rejection_code = $3 where id = $1",
                photoId, state, (object?)code ?? DBNull.Value);
        }

        // Separate application host against the same expanded database: no startup migration or in-memory state.
        await using var reader = new DuoraWebFactory(_connectionString);
        var scenario = new MediaScenario(_connectionString, reader);
        using var owner = await scenario.Identity.SignedInBrowserAsync(account);
        using var status = await owner.GetAsync($"/api/v1/me/photos/{photoId}/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("no-store", status.Headers.CacheControl?.ToString());
        var body = await MediaScenario.ReadObjectAsync(status);
        Assert.Equal(["photoId", "rejectionCode", "status"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(state, (string?)body["status"]);
        Assert.Equal(code, (string?)body["rejectionCode"]);

        using var replay = await MediaScenario.UploadAsync(owner, bytes, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayBody = await MediaScenario.ReadObjectAsync(replay);
        Assert.Equal(photoId, Guid.Parse((string)replayBody["photoId"]!));
        Assert.Equal(state, (string?)replayBody["status"]);
        using var conflict = await MediaScenario.UploadAsync(owner, MediaScenario.JpegLike(fill: 3), idempotencyKey: key);
        await ProblemAssert.IsProblemAsync(conflict, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");

        using var stranger = await scenario.Identity.SignedInBrowserAsync(await scenario.Identity.CreateConfirmedAccountAsync());
        using var foreign = await stranger.GetAsync($"/api/v1/me/photos/{photoId}/status");
        using var missing = await stranger.GetAsync($"/api/v1/me/photos/{Guid.NewGuid()}/status");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Single(await scenario.UploadsOfAsync(account.UserId));
        Assert.Single(await scenario.ProcessingEventsAsync(photoId));
        Assert.Equal(1, scenario.StoredObjectCount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldReviewWaitsStillOccupyAccountAndGlobalSlots(bool global)
    {
        await using var factory = new DuoraWebFactory(_connectionString)
        {
            Settings = new Dictionary<string, string?> { ["Media:MaxQueuedUploads"] = global ? "3" : "100" },
        };
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var owner = await media.Identity.SignedInBrowserAsync(account);
        for (var i = 0; i < 3; i++)
        {
            using var accepted = await MediaScenario.UploadAsync(owner, MediaScenario.JpegLike(fill: (byte)i));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }
        await media.ExecuteAsync("""
            update media.uploads set state = 'AwaitingReview', created_at = created_at - interval '4 days'
            where owner_id = $1
            """, account.UserId);
        using var other = global
            ? await media.Identity.SignedInBrowserAsync(await media.Identity.CreateConfirmedAccountAsync())
            : await media.Identity.SignedInBrowserAsync(account);
        using var refused = await MediaScenario.UploadAsync(other, MediaScenario.JpegLike());
        await ProblemAssert.IsProblemAsync(refused,
            global ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests,
            global ? "MEDIA_QUEUE_FULL" : "UPLOAD_PENDING_LIMIT");
        ProblemAssert.RetryAfterSeconds(refused);
        var released = (await media.UploadsOfAsync(account.UserId))[0].Id;
        await media.ExecuteAsync("update media.uploads set state = 'Rejected', rejection_code = 'PHOTO_REVIEW_EXPIRED' where id = $1", released);
        using var admitted = await MediaScenario.UploadAsync(other, MediaScenario.JpegLike());
        Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode);
    }

    [Theory]
    [InlineData("AwaitingReview", "PHOTO_REVIEW_REJECTED")]
    [InlineData("Approved", "PHOTO_REVIEW_EXPIRED")]
    [InlineData("Rejected", "PHOTO_REVIEW_typo")]
    [InlineData("AwaitingReviewTypo", null)]
    [InlineData("Rejected", null)]
    public async Task InvalidStatesAndResultCombinationsStayClosed(string state, string? code)
    {
        await using var factory = new DuoraWebFactory(_connectionString);
        var media = new MediaScenario(_connectionString, factory);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var owner = await media.Identity.SignedInBrowserAsync(account);
        using var created = await MediaScenario.UploadAsync(owner, MediaScenario.JpegLike());
        var photoId = Assert.Single(await media.UploadsOfAsync(account.UserId)).Id;
        var error = await Assert.ThrowsAsync<PostgresException>(() => media.ExecuteAsync(
            "update media.uploads set state = $2, rejection_code = $3 where id = $1", photoId, state, (object?)code ?? DBNull.Value));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("Quarantined", Assert.Single(await media.UploadsOfAsync(account.UserId)).State);
    }
}
