using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Duora.Web.Safety;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// T-27 (INV-03, INV-06) na revisão humana de foto: atribuir e decidir de novo, com a mesma <c>Idempotency-Key</c>, o
/// mesmo <c>If-Match</c> e o mesmo corpo, depois de perder a permissão ou o TOTP recente, não devolve a resposta guardada
/// nem grava outra. A permissão explícita é lida ao vivo antes do recibo.
/// </summary>
public sealed partial class PhotoReviewApprovalTests
{
    [Fact]
    [Trait("Acceptance", "T-27")]
    public async Task TheSameAssignmentKeyAfterTheAssignPermissionIsRevokedIsRefusedWithoutTheStoredAssignment()
    {
        await using var web = ReviewWeb();
        var (moderator, browser, item) = await ReviewerWithCaseAsync(web);
        using var lifetime = browser;
        var key = Guid.NewGuid().ToString("N");
        var headers = KeyedReviewCommand(PhotoReviewHeaders.Tag("case", item.CaseId, 1), key);
        var path = $"/api/v1/admin/photo-reviews/{item.CaseId}/assignments";
        using var first = await browser.PostJsonAsync(path, new { reviewerId = moderator.UserId }, headers);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await GrantAsync(moderator.UserId, PhotoReviewCapability.Assign, false);

        using var replay = await browser.PostJsonAsync(path, new { reviewerId = moderator.UserId }, headers);

        var body = (await ProblemAssert.IsProblemAsync(replay, HttpStatusCode.Forbidden, "PHOTO_REVIEW_FORBIDDEN")).ToJsonString();
        AssertWithout(body, item.CaseId, item.UploadId, moderator.UserId);
        Assert.Null(replay.Headers.ETag);
        Assert.Equal(2, await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_assignments where case_id=$1", item.CaseId));
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_commands where case_id=$1", item.CaseId));
    }

    [Theory]
    [Trait("Acceptance", "T-27")]
    [InlineData("approve", "meets_policy", false, HttpStatusCode.Forbidden, "PHOTO_REVIEW_FORBIDDEN")]
    [InlineData("approve", "meets_policy", true, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED")]
    [InlineData("reject", "does_not_meet_policy", false, HttpStatusCode.Forbidden, "PHOTO_REVIEW_FORBIDDEN")]
    [InlineData("reject", "does_not_meet_policy", true, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED")]
    public async Task TheSameDecisionKeyAfterLosingAuthorizationIsRefusedWithoutTheStoredDecision(
        string verdict, string reasonCode, bool totpNoLongerRecent, HttpStatusCode expectedStatus, string expectedCode)
    {
        await using var web = ReviewWeb();
        var (moderator, browser, item) = await ReviewerWithCaseAsync(web);
        using var lifetime = browser;
        var path = $"/api/v1/admin/photo-reviews/{item.CaseId}";
        using var assignment = await browser.PostJsonAsync(path + "/assignments", new { reviewerId = moderator.UserId },
            KeyedReviewCommand(PhotoReviewHeaders.Tag("case", item.CaseId, 1), Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, assignment.StatusCode);
        var assigned = JsonNode.Parse(await assignment.Content.ReadAsStringAsync(Ct))!["eTag"]!.GetValue<string>();
        using var image = await browser.GetAsync(path + "/evidence/full");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        var headers = KeyedReviewCommand(assigned, Guid.NewGuid().ToString("N"));
        var decision = new { decision = verdict, reasonCode };
        using var first = await browser.PostJsonAsync(path + "/decisions", decision, headers);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var stored = JsonNode.Parse(await first.Content.ReadAsStringAsync(Ct))!;
        Guid[] leaked = stored["assetId"] is { } asset
            ? [item.CaseId, item.UploadId, Guid.Parse(asset.GetValue<string>())]
            : [item.CaseId, item.UploadId];
        if (totpNoLongerRecent)
        {
            await StepUpClock.ExpireRecentAuthenticationAsync(browser, _time);
        }
        else
        {
            await GrantAsync(moderator.UserId, PhotoReviewCapability.Decide, false);
        }

        using var replay = await browser.PostJsonAsync(path + "/decisions", decision, headers);

        var body = (await ProblemAssert.IsProblemAsync(replay, expectedStatus, expectedCode)).ToJsonString();
        ReplayLeakAssert.ContainsNone(replay, body, leaked);
        Assert.Null(replay.Headers.ETag);
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(verdict == "approve" ? 1L : 0L,
            await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    private async Task<(TestAccount Moderator, Browser Browser, CaseData Item)> ReviewerWithCaseAsync(DuoraWebFactory web)
    {
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        var moderator = await identity.CreatePrivilegedAccountAsync(AccountRoles.Moderator);
        await ExecuteAsync("update identity.users set status='Active' where id=$1", moderator.UserId);
        var browser = await identity.SignedInBrowserAsync(moderator);
        await MfaScenario.EnableAsync(browser, _time);
        foreach (var capability in new[] { PhotoReviewCapability.Assign, PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide })
        {
            await GrantAsync(moderator.UserId, capability, true);
        }

        return (moderator, browser, await HttpCaseAsync(owner.UserId));
    }

    private static MutationHeaders KeyedReviewCommand(string tag, string key) => new(Extra: new Dictionary<string, string>
    {
        ["If-Match"] = tag,
        ["Idempotency-Key"] = key,
    });

    private static void AssertWithout(string body, params Guid[] ids)
    {
        foreach (var id in ids)
        {
            Assert.DoesNotContain(id.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(id.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
