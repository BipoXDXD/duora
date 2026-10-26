using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Duora.IntegrationTests.Media;
using Duora.Web.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    public async Task RealCookiesMfaCsrfAndIndependentAppealReachTheSameGuardedCoordinators()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        using var ownerBrowser = await identity.SignedInBrowserAsync(owner);
        var initial = await identity.CreatePrivilegedAccountAsync(AccountRoles.Moderator);
        var independent = await identity.CreatePrivilegedAccountAsync(AccountRoles.Moderator);
        await ExecuteAsync("update identity.users set status='Active' where id in ($1,$2)", initial.UserId, independent.UserId);
        using var originalBrowser = await identity.SignedInBrowserAsync(initial);
        using var reviewerBrowser = await identity.SignedInBrowserAsync(independent);
        await MfaScenario.EnableAsync(originalBrowser, _time);
        await MfaScenario.EnableAsync(reviewerBrowser, _time);
        foreach (var capability in new[] { PhotoReviewCapability.Assign, PhotoReviewCapability.Evidence,
                     PhotoReviewCapability.Decide, PhotoReviewCapability.Appeal })
        {
            await GrantAsync(initial.UserId, capability, true);
        }
        await GrantAsync(independent.UserId, PhotoReviewCapability.Evidence, true);
        await GrantAsync(independent.UserId, PhotoReviewCapability.Appeal, true);
        var item = await HttpCaseAsync(owner.UserId);
        var casePath = $"/api/v1/admin/photo-reviews/{item.CaseId}";
        using var assignment = await originalBrowser.PostJsonAsync(casePath + "/assignments",
            new { reviewerId = initial.UserId }, ReviewCommand("case", item.CaseId, 1));
        Assert.Equal(HttpStatusCode.OK, assignment.StatusCode);
        var assigned = JsonNode.Parse(await assignment.Content.ReadAsStringAsync(Ct))!;
        using var rejection = await originalBrowser.PostJsonAsync(casePath + "/decisions",
            new { decision = "reject", reasonCode = "does_not_meet_policy" }, ReviewCommand(assigned["eTag"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.OK, rejection.StatusCode);
        using var history = await ownerBrowser.GetAsync($"/api/v1/me/photos/{item.UploadId}/review-history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var historyBody = JsonNode.Parse(await history.Content.ReadAsStringAsync(Ct))!;
        Assert.True(historyBody["canRequestAppeal"]!.GetValue<bool>());
        var requestHeaders = ReviewCommand(historyBody["photoETag"]!.GetValue<string>());
        using var appealRequest = await ownerBrowser.PostJsonAsync($"/api/v1/me/photos/{item.UploadId}/appeals", null, requestHeaders);
        Assert.Equal(HttpStatusCode.Created, appealRequest.StatusCode);
        using var replay = await ownerBrowser.PostJsonAsync($"/api/v1/me/photos/{item.UploadId}/appeals", null, requestHeaders);
        Assert.Equal(await appealRequest.Content.ReadAsStringAsync(Ct), await replay.Content.ReadAsStringAsync(Ct));
        var appeal = JsonNode.Parse(await appealRequest.Content.ReadAsStringAsync(Ct))!["appealId"]!.GetValue<Guid>();
        var appealPath = $"/api/v1/admin/photo-appeals/{appeal}";
        using var routing = await originalBrowser.PostJsonAsync(appealPath + "/assignments",
            new { reviewerId = independent.UserId }, ReviewCommand("appeal", appeal, 1));
        Assert.Equal(HttpStatusCode.OK, routing.StatusCode);
        using var forbiddenOriginal = await originalBrowser.GetAsync(appealPath + "/evidence/full");
        Assert.Equal(HttpStatusCode.NotFound, forbiddenOriginal.StatusCode);
        using var image = await reviewerBrowser.GetAsync(appealPath + "/evidence/full");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/webp", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", image.Headers.CacheControl?.ToString());
        Assert.False(image.Headers.Contains("Accept-Ranges"));
        var routingBody = JsonNode.Parse(await routing.Content.ReadAsStringAsync(Ct))!;
        using var approved = await reviewerBrowser.PostJsonAsync(appealPath + "/decisions",
            new { decision = "approve", reasonCode = "meets_policy" }, ReviewCommand(routingBody["eTag"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var approvedBody = JsonNode.Parse(await approved.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("approve", approvedBody["decision"]!.GetValue<string>());
        Assert.NotNull(approvedBody["assetId"]);
        using var finalHistory = await ownerBrowser.GetAsync($"/api/v1/me/photos/{item.UploadId}/review-history");
        var finalBody = JsonNode.Parse(await finalHistory.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("ClosedApproved", finalBody["appeal"]!["state"]!.GetValue<string>());
        Assert.False(finalBody["canRequestAppeal"]!.GetValue<bool>());
        Assert.Equal(2, finalBody["items"]!.AsArray().Count);
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1 and appeal_id is null and decision='reject'", item.CaseId));
    }

    [Fact]
    public async Task OwnerHistoryMasksOtherOwnersAndCommandsRejectExtraBodyCsrfAndForeignEtags()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        var other = await identity.CreateConfirmedAccountAsync();
        using var browser = await identity.SignedInBrowserAsync(owner);
        using var outsider = await identity.SignedInBrowserAsync(other);
        var item = await HttpCaseAsync(owner.UserId);
        var path = $"/api/v1/me/photos/{item.UploadId}";
        using var foreign = await outsider.GetAsync(path + "/review-history");
        using var absent = await outsider.GetAsync($"/api/v1/me/photos/{Guid.NewGuid()}/review-history");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(foreign.StatusCode, absent.StatusCode);
        using var extraBody = await browser.PostJsonAsync(path + "/appeals", new { arbitrary = "refused" }, ReviewCommand("photo", item.UploadId, 3));
        Assert.Equal(HttpStatusCode.BadRequest, extraBody.StatusCode);
        using var missing = await browser.PostJsonAsync(path + "/appeals", null);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var foreignTag = await browser.PostJsonAsync(path + "/appeals", null, ReviewCommand("case", item.CaseId, 3));
        Assert.Equal(HttpStatusCode.PreconditionFailed, foreignTag.StatusCode);
        using var csrf = await browser.PostJsonAsync(path + "/appeals", null,
            ReviewCommand("photo", item.UploadId, 3) with { SendCsrfToken = false });
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_appeals where case_id=$1", item.CaseId));
    }

    [Fact]
    public async Task StorageFailureBeforeAnEvidenceLeaseIs503WithRetryAfter()
    {
        await using var web = ReviewWeb(failStorage: true);
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        var reviewer = await identity.CreatePrivilegedAccountAsync(AccountRoles.Moderator);
        await ExecuteAsync("update identity.users set status='Active' where id=$1", reviewer.UserId);
        using var browser = await identity.SignedInBrowserAsync(reviewer);
        await MfaScenario.EnableAsync(browser, _time);
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Assign, true);
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Evidence, true);
        await GrantAsync(reviewer.UserId, PhotoReviewCapability.Decide, true);
        var item = await HttpCaseAsync(owner.UserId);
        var path = $"/api/v1/admin/photo-reviews/{item.CaseId}";
        using var assignment = await browser.PostJsonAsync(path + "/assignments", new { reviewerId = reviewer.UserId }, ReviewCommand("case", item.CaseId, 1));
        Assert.Equal(HttpStatusCode.OK, assignment.StatusCode);
        using var response = await browser.GetAsync(path + "/evidence/full");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
    }

    private DuoraWebFactory ReviewWeb(bool failStorage = false) => new(_connectionString)
    {
        Time = _time,
        Settings = new Dictionary<string, string?> { ["Media:Storage:FileSystemRoot"] = _root.FullName },
        TestServices = services =>
        {
            if (failStorage)
            {
                services.AddSingleton<IObjectStore>(new RefuseHttpEvidenceStore());
            }
        },
    };

    private static MutationHeaders ReviewCommand(string kind, Guid id, long version) =>
        ReviewCommand(PhotoReviewHeaders.Tag(kind, id, version));

    private static MutationHeaders ReviewCommand(string tag) => new(Extra: new Dictionary<string, string>
    {
        ["If-Match"] = tag,
        ["Idempotency-Key"] = Guid.NewGuid().ToString("N"),
    });

    private async Task<CaseData> HttpCaseAsync(Guid owner)
    {
        var original = TestImages.Jpeg(64, 32);
        var upload = Upload.Reserve(owner, Key(), PhotoFormat.Jpeg, original.Length, _time.GetUtcNow());
        var store = new FileSystemObjectStore(_root.FullName);
        await using (var db = Context())
        {
            db.Add(upload);
            await db.SaveChangesAsync(Ct);
            var hash = System.Security.Cryptography.SHA256.HashData(original);
            await db.Database.ExecuteSqlAsync($"update media.uploads set state='Processing',version=version+1,content_sha256={hash} where id={upload.Id}", Ct);
        }
        await using (var stream = new MemoryStream(original))
        {
            await store.PutAsync(ObjectKey.Parse(upload.QuarantineKey), stream, original.Length, "image/jpeg", Ct);
        }
        await using (var db = Context())
        {
            Assert.IsType<ReviewCandidateStageResult.Staged>(await new PhotoReviewCandidates(db, store, _time, new MediaProcessingOptions())
                .StageAsync(upload.Id, 2, _time.GetUtcNow().AddMinutes(1), new SanitizeOutcome.Sanitized(PhotoFormat.Jpeg, 64, 32,
                    [.. PhotoVariants.All.Select(k => new SanitizedVariant(k, TestImages.WebP(64, 32), 64, 32))]), Ct));
        }
        await using var read = Context();
        return await read.Database.SqlQuery<CaseData>($"""
            select r.id as "CaseId",u.owner_id as "OwnerId",u.id as "UploadId",u.version as "UploadVersion",
              r.awaiting_review_at as "AwaitingReviewAt",r.review_deadline as "ReviewDeadline"
            from safety.photo_review_cases r join media.uploads u on u.id=r.upload_id where u.id={upload.Id}
            """).SingleAsync(Ct);
    }

    private sealed class RefuseHttpEvidenceStore : IObjectStore
    {
        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken ct) => throw new IOException("Synthetic storage refusal before lease.");
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken ct) => throw new InvalidOperationException();
        public Task DeleteAsync(ObjectKey key, CancellationToken ct) => throw new InvalidOperationException();
    }
}
