using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Duora.IntegrationTests.Media;
using Duora.Web.Safety;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Células da matriz de autorização de docs/api/media-review.md (#173) que só tinham a leitura do código como
/// evidência: conta privilegiada nas rotas de participante, participante e <c>product_admin</c>/<c>operator</c> nas
/// rotas administrativas com o upload ligado, e as recusas e leituras do moderador. Cada teste afirma o status, o
/// código do problema e que o corpo não traz dado alheio, e confere que nada mudou no banco nem no armazenamento.
/// </summary>
public sealed partial class PhotoReviewApprovalTests
{
    private const string ReviewAdminRoute = "/api/v1/admin/photo-reviews";

    private static readonly PhotoReviewCapability[] ReviewerGrants =
        [PhotoReviewCapability.Assign, PhotoReviewCapability.Evidence, PhotoReviewCapability.Decide];

    [Theory]
    [InlineData(AccountRoles.Moderator)]
    [InlineData(AccountRoles.ProductAdmin)]
    [InlineData(AccountRoles.Operator)]
    public async Task PrivilegedAccountsAreForbiddenOnTheOwnerPhotoRoutesAndNothingOfTheOwnerLeaks(string role)
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var (reviewer, item) = await PreparedAsync();
        var approved = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, Key()));
        var files = Files();
        var (_, browser) = await PrivilegedBrowserAsync(identity, role, withReviewGrants: role == AccountRoles.Moderator);
        using var lifetime = browser;
        var probes = OwnerPhotoProbes(item.UploadId, approved.PhotoVersion, approved.AssetId);
        var registered = RegisteredRoutes(web);

        foreach (var (route, send) in probes)
        {
            Assert.Contains(route, registered);
            using var response = await send(browser);
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
            Assert.NotEqual("image/webp", response.Content.Headers.ContentType?.MediaType);
            AssertNoneOf(await response.Content.ReadAsStringAsync(Ct), item.UploadId, approved.AssetId, item.CaseId, item.OwnerId);
        }

        Assert.Equal(1, await ScalarAsync("select count(*) from media.uploads where id=$1 and state='Approved' and version=$2",
            item.UploadId, approved.PhotoVersion));
        Assert.Equal(1, await ScalarAsync("select count(*) from media.media_assets where id=$1 and state='Approved'", approved.AssetId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.photo_tombstones where photo_id=$1", item.UploadId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.photo_deletion_commands where photo_id=$1", item.UploadId));
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task ParticipantsAreForbiddenOnEveryReviewAdminRouteIncludingTheOwnerOfTheCase()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        var outsider = await identity.CreateConfirmedAccountAsync();
        var (moderator, moderatorBrowser) = await PrivilegedBrowserAsync(identity, AccountRoles.Moderator, withReviewGrants: true);
        using var moderatorLifetime = moderatorBrowser;
        var item = await HttpCaseAsync(owner.UserId);
        await AssignOverHttpAsync(moderatorBrowser, moderator.UserId, item);
        var before = await ReviewEffectsAsync(item);
        var probes = ReviewAdminProbes(item, moderator.UserId, caseVersion: 2);
        Assert.Equal(RegisteredRoutes(web).Where(route => route.Contains(ReviewAdminRoute, StringComparison.Ordinal)).Order(StringComparer.Ordinal),
            probes.Select(probe => probe.Route).Order(StringComparer.Ordinal));

        foreach (var account in new[] { owner, outsider })
        {
            using var browser = await identity.SignedInBrowserAsync(account);
            foreach (var (route, send) in probes)
            {
                using var response = await send(browser);
                await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
                Assert.NotEqual("image/webp", response.Content.Headers.ContentType?.MediaType);
                AssertNoneOf(await response.Content.ReadAsStringAsync(Ct), item.CaseId, item.UploadId, moderator.UserId);
            }
        }

        Assert.Equal(before, await ReviewEffectsAsync(item));
    }

    [Theory]
    [InlineData(AccountRoles.ProductAdmin)]
    [InlineData(AccountRoles.Operator)]
    public async Task ProductAdminAndOperatorHaveNoReviewCapabilityAndTheDecisionAsksForRecentMfaFirst(string role)
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        var (moderator, moderatorBrowser) = await PrivilegedBrowserAsync(identity, AccountRoles.Moderator, withReviewGrants: true);
        using var moderatorLifetime = moderatorBrowser;
        var item = await HttpCaseAsync(owner.UserId);
        await AssignOverHttpAsync(moderatorBrowser, moderator.UserId, item);
        var (_, browser) = await PrivilegedBrowserAsync(identity, role, withReviewGrants: false);
        using var lifetime = browser;
        var before = await ReviewEffectsAsync(item);
        var files = Files();
        var probes = ReviewAdminProbes(item, moderator.UserId, caseVersion: 2);

        foreach (var (route, send) in probes)
        {
            using var response = await send(browser);
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "PHOTO_REVIEW_FORBIDDEN");
            Assert.NotEqual("image/webp", response.Content.Headers.ContentType?.MediaType);
            AssertNoneOf(await response.Content.ReadAsStringAsync(Ct), item.CaseId, item.UploadId, item.OwnerId, moderator.UserId);
        }

        await StepUpClock.ExpireRecentAuthenticationAsync(browser, _time);
        var decision = probes.Single(probe => probe.Route.StartsWith("POST", StringComparison.Ordinal) && probe.Route.EndsWith("/decisions", StringComparison.Ordinal));
        using var stale = await decision.Send(browser);
        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        AssertNoneOf(await stale.Content.ReadAsStringAsync(Ct), item.CaseId, item.UploadId);

        Assert.Equal(before, await ReviewEffectsAsync(item));
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task ModeratorSeesOnlyTheCasesAssignedToThemAndEveryOtherCaseAnswersLikeAMissingOne()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        var (moderator, browser) = await PrivilegedBrowserAsync(identity, AccountRoles.Moderator, withReviewGrants: true);
        using var lifetime = browser;
        var assigned = await HttpCaseAsync(owner.UserId);
        var unassigned = await CaseAsync();
        var (_, assignedToSomeoneElse) = await PreparedAsync();
        var assignedTag = await AssignOverHttpAsync(browser, moderator.UserId, assigned);

        using var capabilities = await browser.GetAsync(ReviewAdminRoute + "/capabilities");
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        var granted = JsonNode.Parse(await capabilities.Content.ReadAsStringAsync(Ct))!.AsObject();
        Assert.Equal(["assign", "decide", "evidence", "recentMfaRequiredForDecision"], granted.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.True(granted["assign"]!.GetValue<bool>() && granted["evidence"]!.GetValue<bool>() && granted["decide"]!.GetValue<bool>());
        Assert.False(granted["recentMfaRequiredForDecision"]!.GetValue<bool>());

        using var queue = await browser.GetAsync(ReviewAdminRoute);
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        var queueText = await queue.Content.ReadAsStringAsync(Ct);
        var page = JsonNode.Parse(queueText)!.AsObject();
        var listed = Assert.Single(page["items"]!.AsArray())!.AsObject();
        Assert.Equal(assigned.CaseId, listed["caseId"]!.GetValue<Guid>());
        Assert.Equal(["awaitingReviewAt", "caseId", "eTag", "policyVersion", "reviewDeadline", "state"],
            listed.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Null(page["nextCursor"]);
        AssertNoneOf(queueText, assigned.UploadId, assigned.OwnerId, unassigned.CaseId, unassigned.UploadId, unassigned.OwnerId,
            assignedToSomeoneElse.CaseId, assignedToSomeoneElse.UploadId, assignedToSomeoneElse.OwnerId);

        using var own = await browser.GetAsync($"{ReviewAdminRoute}/{assigned.CaseId}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(assignedTag, Assert.Single(own.Headers.GetValues("ETag")));
        AssertNoneOf(await own.Content.ReadAsStringAsync(Ct), assigned.UploadId, assigned.OwnerId);
        var absent = Guid.NewGuid();
        using var missingCase = await browser.GetAsync($"{ReviewAdminRoute}/{absent}");
        var missingBody = await ProblemAssert.BodyWithoutTraceIdAsync(missingCase);
        await ProblemAssert.IsProblemAsync(missingCase, HttpStatusCode.NotFound, "NOT_FOUND");
        foreach (var other in new[] { unassigned, assignedToSomeoneElse })
        {
            using var foreign = await browser.GetAsync($"{ReviewAdminRoute}/{other.CaseId}");
            await ProblemAssert.IsProblemAsync(foreign, HttpStatusCode.NotFound, "NOT_FOUND");
            Assert.Equal(missingBody, await ProblemAssert.BodyWithoutTraceIdAsync(foreign));
            AssertNoneOf(await foreign.Content.ReadAsStringAsync(Ct), other.CaseId, other.UploadId, other.OwnerId);
        }

        var accessesBefore = await ScalarAsync("select count(*) from safety.photo_review_evidence_accesses");
        foreach (var other in new[] { unassigned, assignedToSomeoneElse })
        {
            using var foreign = await browser.GetAsync($"{ReviewAdminRoute}/{other.CaseId}/evidence/full");
            await ProblemAssert.IsProblemAsync(foreign, HttpStatusCode.NotFound, "NOT_FOUND");
            Assert.NotEqual("image/webp", foreign.Content.Headers.ContentType?.MediaType);
        }
        using var unknownVariant = await browser.GetAsync($"{ReviewAdminRoute}/{assigned.CaseId}/evidence/original");
        await ProblemAssert.IsProblemAsync(unknownVariant, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.NotEqual("image/webp", unknownVariant.Content.Headers.ContentType?.MediaType);
        Assert.Equal(accessesBefore, await ScalarAsync("select count(*) from safety.photo_review_evidence_accesses"));

        using var evidence = await browser.GetAsync($"{ReviewAdminRoute}/{assigned.CaseId}/evidence/full");
        Assert.Equal(HttpStatusCode.OK, evidence.StatusCode);
        Assert.Equal("image/webp", evidence.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", evidence.Headers.CacheControl?.ToString());
        Assert.Equal(TestImages.WebP(64, 32), await evidence.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(1, await ScalarAsync("""
            select count(*) from ops.audit_events e join safety.photo_review_evidence_accesses a on a.id=e.target_id
            where a.case_id=$1 and e.action='safety.photo_review_evidence_viewed' and e.actor_id=$2
            """, assigned.CaseId, moderator.UserId));
        Assert.Equal(accessesBefore + 1, await ScalarAsync("select count(*) from safety.photo_review_evidence_accesses"));
    }

    [Fact]
    public async Task ModeratorAssignmentRefusalsAreNotFoundPreconditionAndConflictOverHttp()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        var deleter = await identity.CreateConfirmedAccountAsync();
        using var deleterBrowser = await identity.SignedInBrowserAsync(deleter);
        var (moderator, browser) = await PrivilegedBrowserAsync(identity, AccountRoles.Moderator, withReviewGrants: true);
        using var lifetime = browser;
        var assigned = await HttpCaseAsync(owner.UserId);
        var untouched = await CaseAsync();
        var deleted = await HttpCaseAsync(deleter.UserId);
        using (var removal = await PhotoDeletionTests.DeleteAsync(deleterBrowser, deleted.UploadId,
            PhotoReviewHeaders.Tag("photo", deleted.UploadId, deleted.UploadVersion), Guid.NewGuid().ToString("N")))
        {
            Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);
        }
        var key = Guid.NewGuid().ToString("N");
        var body = new { reviewerId = moderator.UserId };
        using (var first = await browser.PostJsonAsync($"{ReviewAdminRoute}/{assigned.CaseId}/assignments", body, WithKey("case", assigned.CaseId, 1, key)))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        var untouchedBefore = await ReviewEffectsAsync(untouched);
        var assignedBefore = await ReviewEffectsAsync(assigned);
        var deletedBefore = await ReviewEffectsAsync(deleted);
        var absent = Guid.NewGuid();

        using var missing = await browser.PostJsonAsync($"{ReviewAdminRoute}/{absent}/assignments", body, ReviewCommand("case", absent, 1));
        using var withoutPrecondition = await browser.PostJsonAsync($"{ReviewAdminRoute}/{untouched.CaseId}/assignments", body,
            new MutationHeaders(Extra: new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") }));
        using var stale = await browser.PostJsonAsync($"{ReviewAdminRoute}/{assigned.CaseId}/assignments", body, ReviewCommand("case", assigned.CaseId, 1));
        using var otherResourceTag = await browser.PostJsonAsync($"{ReviewAdminRoute}/{untouched.CaseId}/assignments", body,
            ReviewCommand("case", assigned.CaseId, 1));
        using var reusedKey = await browser.PostJsonAsync($"{ReviewAdminRoute}/{untouched.CaseId}/assignments", body, WithKey("case", untouched.CaseId, 1, key));
        using var closedCase = await browser.PostJsonAsync($"{ReviewAdminRoute}/{deleted.CaseId}/assignments", body, ReviewCommand("case", deleted.CaseId, 1));

        await ProblemAssert.IsProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.IsProblemAsync(withoutPrecondition, HttpStatusCode.PreconditionRequired, "PRECONDITION_REQUIRED");
        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        await ProblemAssert.IsProblemAsync(otherResourceTag, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        await ProblemAssert.IsProblemAsync(reusedKey, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        await ProblemAssert.IsProblemAsync(closedCase, HttpStatusCode.Conflict, "PHOTO_REVIEW_STATE_CONFLICT");
        foreach (var refused in new[] { missing, withoutPrecondition, stale, otherResourceTag, reusedKey, closedCase })
        {
            Assert.Null(refused.Headers.ETag);
            AssertNoneOf(await refused.Content.ReadAsStringAsync(Ct), untouched.UploadId, untouched.OwnerId, deleted.UploadId, deleted.OwnerId);
        }
        Assert.Equal(untouchedBefore, await ReviewEffectsAsync(untouched));
        Assert.Equal(assignedBefore, await ReviewEffectsAsync(assigned));
        Assert.Equal(deletedBefore, await ReviewEffectsAsync(deleted));
    }

    [Fact]
    public async Task ModeratorDecisionWithoutARecentSecondFactorIsReauthenticationRequiredAndDecidesNothing()
    {
        await using var web = ReviewWeb();
        var identity = new IdentityScenario(_connectionString, web);
        var owner = await identity.CreateConfirmedAccountAsync();
        var (moderator, browser) = await PrivilegedBrowserAsync(identity, AccountRoles.Moderator, withReviewGrants: true);
        using var lifetime = browser;
        var item = await HttpCaseAsync(owner.UserId);
        await AssignOverHttpAsync(browser, moderator.UserId, item);
        var before = await ReviewEffectsAsync(item);
        var files = Files();

        await StepUpClock.ExpireRecentAuthenticationAsync(browser, _time);
        using var stale = await browser.PostJsonAsync($"{ReviewAdminRoute}/{item.CaseId}/decisions",
            new { decision = "approve", reasonCode = "meets_policy" }, ReviewCommand("case", item.CaseId, 2));
        using var capabilities = await browser.GetAsync(ReviewAdminRoute + "/capabilities");

        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        AssertNoneOf(await stale.Content.ReadAsStringAsync(Ct), item.CaseId, item.UploadId, item.OwnerId);
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        Assert.True(JsonNode.Parse(await capabilities.Content.ReadAsStringAsync(Ct))!["recentMfaRequiredForDecision"]!.GetValue<bool>());
        Assert.Equal(before, await ReviewEffectsAsync(item));
        Assert.Equal(files, Files());
    }

    private async Task<(TestAccount Account, Browser Browser)> PrivilegedBrowserAsync(IdentityScenario identity, string role, bool withReviewGrants)
    {
        var account = await identity.CreatePrivilegedAccountAsync(role);
        await ExecuteAsync("update identity.users set status='Active' where id=$1", account.UserId);
        var browser = await identity.SignedInBrowserAsync(account);
        await MfaScenario.EnableAsync(browser, _time);
        if (withReviewGrants)
        {
            foreach (var capability in ReviewerGrants)
            {
                await GrantAsync(account.UserId, capability, true);
            }
        }
        return (account, browser);
    }

    private static async Task<string> AssignOverHttpAsync(Browser moderator, Guid reviewer, CaseData item)
    {
        using var response = await moderator.PostJsonAsync($"{ReviewAdminRoute}/{item.CaseId}/assignments", new { reviewerId = reviewer },
            ReviewCommand("case", item.CaseId, 1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tag = Assert.Single(response.Headers.GetValues("ETag"));
        Assert.Equal(PhotoReviewHeaders.Tag("case", item.CaseId, 2), tag);
        return tag;
    }

    private static MutationHeaders WithKey(string kind, Guid id, long version, string key) => new(Extra: new Dictionary<string, string>
    {
        ["If-Match"] = PhotoReviewHeaders.Tag(kind, id, version),
        ["Idempotency-Key"] = key,
    });

    private static (string Route, Func<Browser, Task<HttpResponseMessage>> Send)[] OwnerPhotoProbes(Guid photoId, long photoVersion, Guid assetId) =>
    [
        ("GET /api/v1/me/photos", browser => browser.GetAsync("/api/v1/me/photos")),
        ("GET /api/v1/me/photos/{photoId:guid}/review-history", browser => browser.GetAsync($"/api/v1/me/photos/{photoId}/review-history")),
        ("DELETE /api/v1/me/photos/{photoId:guid}", browser => PhotoDeletionTests.DeleteAsync(browser, photoId,
            PhotoReviewHeaders.Tag("photo", photoId, photoVersion), Guid.NewGuid().ToString("N"))),
        ("GET /api/v1/media/{assetId:guid}", browser => browser.GetAsync($"/api/v1/media/{assetId}")),
    ];

    private static (string Route, Func<Browser, Task<HttpResponseMessage>> Send)[] ReviewAdminProbes(CaseData item, Guid reviewer, long caseVersion)
    {
        var path = $"{ReviewAdminRoute}/{item.CaseId}";
        return
        [
            ($"GET {ReviewAdminRoute}/capabilities", browser => browser.GetAsync(ReviewAdminRoute + "/capabilities")),
            ($"GET {ReviewAdminRoute}", browser => browser.GetAsync(ReviewAdminRoute)),
            ($"GET {ReviewAdminRoute}/{{caseId:guid}}", browser => browser.GetAsync(path)),
            ($"POST {ReviewAdminRoute}/{{caseId:guid}}/assignments", browser => browser.PostJsonAsync(path + "/assignments",
                new { reviewerId = reviewer }, ReviewCommand("case", item.CaseId, caseVersion))),
            ($"GET {ReviewAdminRoute}/{{caseId:guid}}/evidence/{{variant}}", browser => browser.GetAsync(path + "/evidence/full")),
            ($"POST {ReviewAdminRoute}/{{caseId:guid}}/decisions", browser => browser.PostJsonAsync(path + "/decisions",
                new { decision = "approve", reasonCode = "meets_policy" }, ReviewCommand("case", item.CaseId, caseVersion))),
        ];
    }

    private static List<string> RegisteredRoutes(DuoraWebFactory web) =>
    [
        .. web.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => $"{method} /{(endpoint.RoutePattern.RawText ?? "").Trim('/')}")),
    ];

    private static void AssertNoneOf(string body, params Guid[] ids)
    {
        foreach (var id in ids)
        {
            Assert.DoesNotContain(id.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(id.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Versão do caso e da foto, linhas de atribuição, decisão, comando e acesso, e a auditoria de revisão.</summary>
    private async Task<string> ReviewEffectsAsync(CaseData item) => string.Join(',',
    [
        await ScalarAsync("select version from safety.photo_review_cases where id=$1", item.CaseId),
        await ScalarAsync("select version from media.uploads where id=$1", item.UploadId),
        await ScalarAsync("select count(*) from safety.photo_review_assignments where case_id=$1", item.CaseId),
        await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId),
        await ScalarAsync("select count(*) from safety.photo_review_commands where case_id=$1", item.CaseId),
        await ScalarAsync("select count(*) from safety.photo_review_evidence_accesses where case_id=$1", item.CaseId),
        await ScalarAsync("select count(*) from ops.audit_events where action like 'safety.photo_review%'"),
    ]);
}
