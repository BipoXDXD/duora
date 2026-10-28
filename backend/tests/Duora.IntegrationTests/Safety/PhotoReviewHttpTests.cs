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
    public async Task SingleModeratorRejectsThenANewUploadOpensANewReviewThatTheSameModeratorApproves()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        using var ownerBrowser = await identity.SignedInBrowserAsync(owner);
        var moderator = await identity.CreatePrivilegedAccountAsync(AccountRoles.Moderator);
        await ExecuteAsync("update identity.users set status='Active' where id=$1", moderator.UserId);
        using var moderatorBrowser = await identity.SignedInBrowserAsync(moderator);
        await MfaScenario.EnableAsync(moderatorBrowser, _time);
        foreach (var capability in new[] { PhotoReviewCapability.Assign, PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide })
        {
            await GrantAsync(moderator.UserId, capability, true);
        }

        var rejected = await HttpCaseAsync(owner.UserId);
        var rejection = await DecideOverHttpAsync(moderatorBrowser, moderator.UserId, rejected.CaseId, "reject", "does_not_meet_policy");
        Assert.Null(rejection["assetId"]);
        using var history = await ownerBrowser.GetAsync($"/api/v1/me/photos/{rejected.UploadId}/review-history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var historyBody = JsonNode.Parse(await history.Content.ReadAsStringAsync(Ct))!.AsObject();
        Assert.Equal(["items", "photoETag", "photoId"], historyBody.Select(p => p.Key).Order(StringComparer.Ordinal));
        var decision = Assert.Single(historyBody["items"]!.AsArray())!.AsObject();
        Assert.Equal(["decidedAt", "decision", "decisionId", "reasonCode"], decision.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("reject", decision["decision"]!.GetValue<string>());

        var replacement = await HttpCaseAsync(owner.UserId);
        Assert.NotEqual(rejected.CaseId, replacement.CaseId);
        var approval = await DecideOverHttpAsync(moderatorBrowser, moderator.UserId, replacement.CaseId, "approve", "meets_policy");
        Assert.NotNull(approval["assetId"]);
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1 and decision='reject'", rejected.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1 and decision='approve'", replacement.CaseId));
    }

    [Fact]
    public async Task AppealRoutesNoLongerExistForOwnersOrModerators()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        using var browser = await identity.SignedInBrowserAsync(owner);
        var item = await HttpCaseAsync(owner.UserId);
        using var request = await browser.PostJsonAsync($"/api/v1/me/photos/{item.UploadId}/appeals", null,
            ReviewCommand("photo", item.UploadId, item.UploadVersion));
        Assert.Equal(HttpStatusCode.NotFound, request.StatusCode);
        var routes = web.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? "");
        Assert.DoesNotContain(routes, route => route.Contains("appeal", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_appeals where case_id=$1", item.CaseId));
    }

    [Fact]
    public async Task OwnerHistoryMasksOtherOwnersWithTheSameNotFound()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        var other = await identity.CreateConfirmedAccountAsync();
        using var outsider = await identity.SignedInBrowserAsync(other);
        var item = await HttpCaseAsync(owner.UserId);
        using var foreign = await outsider.GetAsync($"/api/v1/me/photos/{item.UploadId}/review-history");
        using var absent = await outsider.GetAsync($"/api/v1/me/photos/{Guid.NewGuid()}/review-history");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(foreign.StatusCode, absent.StatusCode);
        Assert.Equal(await ProblemAssert.BodyWithoutTraceIdAsync(absent), await ProblemAssert.BodyWithoutTraceIdAsync(foreign));
        Assert.DoesNotContain(item.UploadId.ToString(), await foreign.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonNode> DecideOverHttpAsync(Browser browser, Guid moderator, Guid caseId, string decision, string reason)
    {
        var path = $"/api/v1/admin/photo-reviews/{caseId}";
        using var assignment = await browser.PostJsonAsync(path + "/assignments", new { reviewerId = moderator }, ReviewCommand("case", caseId, 1));
        Assert.Equal(HttpStatusCode.OK, assignment.StatusCode);
        var assigned = JsonNode.Parse(await assignment.Content.ReadAsStringAsync(Ct))!;
        using var image = await browser.GetAsync(path + "/evidence/full");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("no-store", image.Headers.CacheControl?.ToString());
        using var decided = await browser.PostJsonAsync(path + "/decisions", new { decision, reasonCode = reason },
            ReviewCommand(assigned["eTag"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        var body = JsonNode.Parse(await decided.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(decision, body["decision"]!.GetValue<string>());
        Assert.Null(body.AsObject()["appealId"]);
        return body;
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
