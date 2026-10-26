using System.Net;
using DotNet.Testcontainers.Builders;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Media;

public sealed class MediaReviewPublishedRollbackTests
{
    [Fact]
    public async Task PublishedBinaryReadsExpandedStatesWithoutLosingReplayIsolationOrQuotas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync(cancellationToken);
        await using var postgres = new PostgresFixture(network);
        await postgres.InitializeAsync();
        var connectionString = postgres.ConnectionString;
        var containerConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = "rollback-postgres",
            Port = 5432,
        }.ConnectionString;
        var samples = new List<Sample>();
        TestAccount limited;
        TestAccount stranger;
        var bytes = MediaScenario.JpegLike();
        // Current migrations/fixtures prepare the expanded database; this host is
        // disposed before the historical published binary touches any record.
        await using (var writer = new DuoraWebFactory(connectionString))
        {
            var media = new MediaScenario(connectionString, writer);
            foreach (var (state, code) in new (string, string?)[]
            {
                ("AwaitingReview", null), ("Rejected", "PHOTO_REVIEW_REJECTED"),
                ("Rejected", "PHOTO_REVIEW_EXPIRED"), ("Rejected", "PHOTO_REVIEW_UNAVAILABLE"),
            })
            {
                var account = await media.Identity.CreateConfirmedAccountAsync();
                using var browser = await media.Identity.SignedInBrowserAsync(account);
                var key = MediaScenario.NewKey();
                using var created = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);
                Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
                var id = Assert.Single(await media.UploadsOfAsync(account.UserId)).Id;
                await media.ExecuteAsync("update media.uploads set state=$2, rejection_code=$3 where id=$1", id, state, (object?)code ?? DBNull.Value);
                samples.Add(new Sample(account, id, key, state, code));
            }
            limited = await media.Identity.CreateConfirmedAccountAsync();
            stranger = await media.Identity.CreateConfirmedAccountAsync();
            using var owner = await media.Identity.SignedInBrowserAsync(limited);
            for (var index = 0; index < 3; index++)
            {
                using var accepted = await MediaScenario.UploadAsync(owner, bytes);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }
            await media.ExecuteAsync("""
                update media.uploads set state='AwaitingReview', created_at=created_at-interval '4 days'
                where owner_id=$1
                """, limited.UserId);
        }

        await using (var historical = await PublishedReviewReader.StartAsync(network, containerConnection))
        {
            using var outsider = historical.CreateBrowser();
            using var outsiderLogin = await IdentityScenario.LoginAsync(outsider, stranger.Email, stranger.Password);
            Assert.Equal(HttpStatusCode.NoContent, outsiderLogin.StatusCode);
            foreach (var sample in samples)
            {
                using var owner = historical.CreateBrowser();
                using var login = await IdentityScenario.LoginAsync(owner, sample.Account.Email, sample.Account.Password);
                Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
                using var status = await owner.GetAsync($"/api/v1/me/photos/{sample.Id}/status");
                Assert.Equal(HttpStatusCode.OK, status.StatusCode);
                Assert.Equal("no-store", status.Headers.CacheControl?.ToString());
                var result = await MediaScenario.ReadObjectAsync(status);
                Assert.Equal(sample.State, (string?)result["status"]);
                Assert.Equal(sample.Code, (string?)result["rejectionCode"]);
                using var replay = await UploadToPublishedAsync(owner, bytes, sample.Key);
                Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
                var replayBody = await MediaScenario.ReadObjectAsync(replay);
                Assert.Equal(sample.Id, Guid.Parse((string)replayBody["photoId"]!));
                Assert.Equal(sample.State, (string?)replayBody["status"]);
                using var changed = await UploadToPublishedAsync(owner, MediaScenario.JpegLike(fill: 3), sample.Key);
                await ProblemAssert.IsProblemAsync(changed, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
                using var foreign = await outsider.GetAsync($"/api/v1/me/photos/{sample.Id}/status");
                Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            }
            using var limitedOwner = historical.CreateBrowser();
            using var limitedLogin = await IdentityScenario.LoginAsync(limitedOwner, limited.Email, limited.Password);
            Assert.Equal(HttpStatusCode.NoContent, limitedLogin.StatusCode);
            using var refusal = await UploadToPublishedAsync(limitedOwner, bytes);
            await ProblemAssert.IsProblemAsync(refusal, HttpStatusCode.TooManyRequests, "UPLOAD_PENDING_LIMIT");
            ProblemAssert.RetryAfterSeconds(refusal);
        }
        // Recreate the same old release over the same database; no new build or
        // migration, now with the stricter global quota to prove pending accounting.
        await using (var historical = await PublishedReviewReader.StartAsync(network, containerConnection, queuedLimit: 3))
        {
            using var owner = historical.CreateBrowser();
            using var login = await IdentityScenario.LoginAsync(owner, stranger.Email, stranger.Password);
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
            using var refused = await UploadToPublishedAsync(owner, bytes);
            await ProblemAssert.IsProblemAsync(refused, HttpStatusCode.ServiceUnavailable, "MEDIA_QUEUE_FULL");
            ProblemAssert.RetryAfterSeconds(refused);
        }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var assets = new NpgsqlCommand("select count(*) from media.media_assets", connection);
        Assert.Equal(0L, (long)(await assets.ExecuteScalarAsync(cancellationToken))!);
        await using var uploads = new NpgsqlCommand("select count(*) from media.uploads", connection);
        Assert.Equal(7L, (long)(await uploads.ExecuteScalarAsync(cancellationToken))!);
    }

    // The pinned historical binary uses its public antiforgery contract for uploads.
    private static Task<HttpResponseMessage> UploadToPublishedAsync(Browser browser, byte[] bytes, string? key = null)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/jpeg");
        return browser.PostContentAsync(MediaScenario.PhotosPath, content, new MutationHeaders(
            CsrfTokenOverride: browser.Cookie("XSRF-TOKEN"),
            Extra: new Dictionary<string, string> { ["Idempotency-Key"] = key ?? MediaScenario.NewKey() }));
    }

    private sealed record Sample(TestAccount Account, Guid Id, string Key, string State, string? Code);
}
