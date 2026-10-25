using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    private static readonly string[] OwnerPhotoPublicFields = ["createdAt", "photoId", "rejectionCode", "status"];

    [Fact]
    public async Task OwnerPhotoPagesUseRealIdsAndAStableTieOrderWithoutReturningAnotherOwnersPhotos()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var first = await identity.CreateConfirmedAccountAsync();
        var second = await identity.CreateConfirmedAccountAsync();
        var own = await PhotoListRowsAsync(first.UserId, 3);
        var foreign = await PhotoListRowsAsync(second.UserId, 2);
        await ExecuteAsync("update media.uploads set state='Deleted',version=version+1 where id=$1", own[^1]);
        using var browser = await identity.SignedInBrowserAsync(first);
        var files = Files();
        using var page1 = await browser.GetAsync($"/api/v1/me/photos?limit=1&ownerId={second.UserId}");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var firstBody = JsonNode.Parse(await page1.Content.ReadAsStringAsync(Ct))!;
        var item1 = Assert.Single(firstBody["items"]!.AsArray())!;
        var cursor = firstBody["nextCursor"]!.GetValue<string>();
        Assert.InRange(cursor.Length, 1, 512);
        Assert.DoesNotContain(first.UserId.ToString("N"), cursor, StringComparison.Ordinal);
        using var page2 = await browser.GetAsync("/api/v1/me/photos?limit=1&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);
        var secondBody = JsonNode.Parse(await page2.Content.ReadAsStringAsync(Ct))!;
        var item2 = Assert.Single(secondBody["items"]!.AsArray())!;
        Assert.Null(secondBody["nextCursor"]);
        var returned = new[] { item1["photoId"]!.GetValue<Guid>(), item2["photoId"]!.GetValue<Guid>() };
        // All rows have the same created_at; the database's UUID tiebreaker must paginate without duplication.
        Assert.Equal(2, returned.Distinct().Count());
        Assert.Equal(own.Take(2).Order(), returned);
        Assert.DoesNotContain(returned, id => foreign.Contains(id));
        Assert.Equal(item1["createdAt"]!.GetValue<string>(), item2["createdAt"]!.GetValue<string>());
        Assert.Equal("Pending", item1["status"]!.GetValue<string>());
        Assert.Null(item1["rejectionCode"]);
        Assert.Equal(OwnerPhotoPublicFields, item1.AsObject().Select(p => p.Key).Order());
        Assert.Equal("no-store", page1.Headers.CacheControl?.ToString());
        Assert.Equal(files, Files());

        using var outsider = await identity.SignedInBrowserAsync(second);
        using var foreignCursor = await outsider.GetAsync("/api/v1/me/photos?limit=1&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(HttpStatusCode.BadRequest, foreignCursor.StatusCode);
        using var ownForeignPage = await outsider.GetAsync("/api/v1/me/photos?limit=50");
        var foreignBody = JsonNode.Parse(await ownForeignPage.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(foreign.Order(), foreignBody["items"]!.AsArray().Select(p => p!["photoId"]!.GetValue<Guid>()).Order());
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=-1")]
    [InlineData("limit=51")]
    [InlineData("cursor=bad")]
    [InlineData("cursor=%21%21")]
    [InlineData("cursor=")]
    public async Task OwnerPhotoListRejectsInvalidLimitsAndCursorsWithoutExposingPhotos(string query)
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        _ = await PhotoListRowsAsync(owner.UserId, 1);
        using var browser = await identity.SignedInBrowserAsync(owner);
        using var response = await browser.GetAsync("/api/v1/me/photos?" + query);
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where owner_id=$1", owner.UserId));
    }

    [Fact]
    public async Task OwnerPhotoListRejectsAnOversizedCursorAndInvalidOrRevokedSessions()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        using var browser = await identity.SignedInBrowserAsync(owner);
        using var oversized = await browser.GetAsync("/api/v1/me/photos?cursor=" + new string('a', 513));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        using var anonymous = new Browser(web);
        using var absentSession = await anonymous.GetAsync("/api/v1/me/photos");
        Assert.Equal(HttpStatusCode.Unauthorized, absentSession.StatusCode);
        await ExecuteAsync("update identity.login_sessions set revoked_at=$2 where user_id=$1", owner.UserId, _time.GetUtcNow());
        using var revoked = await browser.GetAsync("/api/v1/me/photos");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task OwnerPhotoListKeepsMediaAdmissionOffAndAnEmptyInventoryIsOnlyAnEmptyPage()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        using var browser = await identity.SignedInBrowserAsync(owner);
        using var empty = await browser.GetAsync("/api/v1/me/photos");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var body = JsonNode.Parse(await empty.Content.ReadAsStringAsync(Ct))!;
        Assert.Empty(body["items"]!.AsArray());
        Assert.Null(body["nextCursor"]);
        await using var disabled = new DuoraWebFactory(_connectionString)
        {
            Time = _time,
            Settings = new Dictionary<string, string?> { ["Media:Review"] = null, ["Media:Storage:Provider"] = null },
        };
        using var disabledBrowser = await new IdentityScenario(_connectionString, disabled).SignedInBrowserAsync(owner);
        using var response = await disabledBrowser.GetAsync("/api/v1/me/photos");
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "MEDIA_UPLOADS_DISABLED");
    }

    private async Task<Guid[]> PhotoListRowsAsync(Guid owner, int count)
    {
        var ids = new List<Guid>();
        await using var db = Context();
        for (var index = 0; index < count; index++)
        {
            var upload = Upload.Reserve(owner, Key(), PhotoFormat.Jpeg, 100, _time.GetUtcNow());
            db.Add(upload);
            ids.Add(upload.Id);
        }
        await db.SaveChangesAsync(Ct);
        return [.. ids];
    }
}
