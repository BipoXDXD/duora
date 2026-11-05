using System.Net;
using System.Text.Json.Nodes;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// API-ADM26 e API-ADM27 (issue #321, ADR-0049, D4): a barreira das rotas de run, com cookie real e sem corte
/// configurado. Sessão com toque, papel <c>moderator</c> lido ao vivo, MFA, TOTP recente, CSRF e
/// <c>Idempotency-Key</c>. Toda recusa acontece sem gravar e o corpo só tem campos de Problem Details.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class RegistrationReconciliationRunHttpTests : IAsyncDisposable
{
    public const string Path = "/api/v1/admin/registration-reconciliations/runs";

    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly string[] ProblemKeys = ["code", "status", "title", "traceId", "type"];

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly AccountSuspensionScenario _scenario;

    public RegistrationReconciliationRunHttpTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
        _scenario = new AccountSuspensionScenario(postgres.ConnectionString, _factory, _time);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    public static object Body(Guid? subject = null, Guid? measure = null, string digest = Digest) => new
    {
        units = new[] { new { subjectUserId = subject ?? Guid.NewGuid(), originalMeasureId = measure ?? Guid.NewGuid(), unitDigest = digest } },
        justification = "Prévia revisada pelo moderador.",
        authorizationReference = "AUTH-321-1",
    };

    private static string RunPath(Guid? run = null) => $"{Path}/{run ?? Guid.NewGuid()}";

    private static MutationHeaders Keyed() => AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey());

    private static async Task AssertBareProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await ProblemAssert.IsProblemAsync(response, status, code);
        Assert.Equal(ProblemKeys, body.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    private Task<long> RunsOfAsync(Guid moderator) =>
        _scenario.CountAsync("select count(*) from ops.registration_reconciliation_runs where created_by_user_id=$1", moderator);

    [Fact]
    public async Task WithoutASessionBothRoutesAreUnauthorized()
    {
        using var browser = new Browser(_factory);
        await browser.FetchCsrfTokenAsync();

        using var create = await browser.PostJsonAsync(Path, Body(), Keyed());
        using var read = await browser.GetWithoutContextAsync(RunPath());

        await AssertBareProblemAsync(create, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertBareProblemAsync(read, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task AParticipantIsForbiddenOnBothRoutes()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        using var browser = await _scenario.Identity.SignedInBrowserAsync(participant);

        using var create = await browser.PostJsonAsync(Path, Body(), Keyed());
        using var read = await browser.GetAsync(RunPath());

        await AssertBareProblemAsync(create, HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertBareProblemAsync(read, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(0, await RunsOfAsync(participant.UserId));
    }

    [Theory]
    [InlineData(AccountRoles.Operator)]
    [InlineData(AccountRoles.ProductAdmin)]
    public async Task AnAdministrativeSessionWithoutTheModeratorRoleIsForbidden(string role)
    {
        var (account, browser) = await _scenario.PrivilegedBrowserAsync(role);
        using var _ = browser;

        using var create = await browser.PostJsonAsync(Path, Body(), Keyed());
        using var read = await browser.GetAsync(RunPath());

        await AssertBareProblemAsync(create, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
        await AssertBareProblemAsync(read, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
        Assert.Equal(0, await RunsOfAsync(account.UserId));
    }

    [Fact]
    public async Task ThePermissionComesBeforeAnyValidationOfTheBodyOrKey()
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync(AccountRoles.Operator);
        using var _ = browser;

        using var response = await browser.PostRawJsonAsync(Path, "{\"justification\":\"\"}");

        await AssertBareProblemAsync(response, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
    }

    [Fact]
    public async Task TheModeratorRoleIsReadLiveOnEveryRequest()
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await _scenario.ExecuteAsync(
            "delete from identity.user_roles where user_id = $1 and role_id = (select id from identity.roles where name = $2)",
            moderator.UserId, AccountRoles.Moderator);
        await _scenario.Identity.GrantRoleAsync(moderator.UserId, AccountRoles.ProductAdmin);

        using var create = await browser.PostJsonAsync(Path, Body(), Keyed());
        using var read = await browser.GetAsync(RunPath());

        await AssertBareProblemAsync(create, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
        await AssertBareProblemAsync(read, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
    }

    [Fact]
    public async Task AnExpiredTotpProofRequiresTheExistingStepUp()
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await StepUpClock.ExpireRecentAuthenticationAsync(browser, _time);

        using var create = await browser.PostJsonAsync(Path, Body(), Keyed());
        using var read = await browser.GetAsync(RunPath());

        await AssertBareProblemAsync(create, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        await AssertBareProblemAsync(read, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        Assert.Equal(0, await RunsOfAsync(moderator.UserId));
    }

    [Fact]
    public async Task ATotpProofInTheFutureIsNotRecent()
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await _scenario.ExecuteAsync(
            "update identity.login_sessions set totp_verified_at = $2 where user_id = $1 and revoked_at is null",
            moderator.UserId, _time.GetUtcNow().AddMinutes(1));

        using var response = await browser.PostJsonAsync(Path, Body(), Keyed());

        await AssertBareProblemAsync(response, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CsrfAndOriginChecksProtectTheCreation(bool crossOrigin)
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await browser.PostJsonAsync(Path, Body(), new MutationHeaders(
            SendCsrfToken: crossOrigin,
            Origin: crossOrigin ? "https://untrusted.invalid" : DuoraWebFactory.PublicOrigin,
            FetchSite: crossOrigin ? "cross-site" : "same-origin",
            Extra: new Dictionary<string, string> { ["Idempotency-Key"] = AccountSuspensionScenario.NewKey() }));

        await AssertBareProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        Assert.Equal(0, await RunsOfAsync(moderator.UserId));
        // O mesmo pedido com a prova chega ao caso de uso: a recusa acima veio do CSRF nesta rota, não de outra coisa.
        using var proved = await browser.PostJsonAsync(Path, Body(), Keyed());
        await AssertBareProblemAsync(proved, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
    }

    [Fact]
    public async Task WithoutThePrivateProofTheReadIsRefused()
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var run = RunPath();

        using var response = await browser.GetAsync(run, privateProofOverride: "forjado");
        using var proved = await browser.GetAsync(run);

        await AssertBareProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        await AssertBareProblemAsync(proved, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tem espaço")]
    [InlineData("kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk")]
    public async Task AMissingOrInvalidIdempotencyKeyIsABadRequest(string? key)
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await browser.PostJsonAsync(Path, Body(),
            key is null ? MutationHeaders.SameOrigin : AccountSuspensionScenario.WithKey(key));

        await AssertBareProblemAsync(response, HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_INVALID");
        Assert.Equal(0, await RunsOfAsync(moderator.UserId));
    }

    public static TheoryData<string, string> InvalidBodies()
    {
        var unit = "{\"subjectUserId\":\"0194f1e0-0000-7000-8000-000000000001\",\"originalMeasureId\":\"0194f1e0-0000-7000-8000-000000000002\",\"unitDigest\":\"" + Digest + "\"}";
        const string Texts = "\"justification\":\"Revisada.\",\"authorizationReference\":\"AUTH-1\"";
        var units = "\"units\":[" + unit + "]";
        return new TheoryData<string, string>
        {
            { "{" + Texts + "}", "units" },
            { "{\"units\":[]," + Texts + "}", "units" },
            { "{\"units\":[" + unit + "," + unit + "]," + Texts + "}", "units" },
            { "{\"units\":[" + string.Join(',', Enumerable.Repeat(unit, 11)) + "]," + Texts + "}", "units" },
            { "{\"units\":[" + unit.Replace(Digest, "abc", StringComparison.Ordinal) + "]," + Texts + "}", "units" },
            { "{\"units\":[" + unit.Replace(Digest, Digest.ToUpperInvariant(), StringComparison.Ordinal) + "]," + Texts + "}", "units" },
            { "{\"units\":[" + unit.Replace("0194f1e0-0000-7000-8000-000000000001", "00000000-0000-0000-0000-000000000000", StringComparison.Ordinal) + "]," + Texts + "}", "units" },
            { "{\"units\":[null]," + Texts + "}", "units" },
            { "{" + units + ",\"authorizationReference\":\"AUTH-1\"}", "justification" },
            { "{" + units + ",\"justification\":\"  \",\"authorizationReference\":\"AUTH-1\"}", "justification" },
            { "{" + units + ",\"justification\":\"linha\\nnova\",\"authorizationReference\":\"AUTH-1\"}", "justification" },
            { "{" + units + ",\"justification\":\"nul\\u0000\",\"authorizationReference\":\"AUTH-1\"}", "justification" },
            { "{" + units + ",\"justification\":\"" + new string('j', 2001) + "\",\"authorizationReference\":\"AUTH-1\"}", "justification" },
            { "{" + units + ",\"justification\":\"Revisada.\"}", "authorizationReference" },
            { "{" + units + ",\"justification\":\"Revisada.\",\"authorizationReference\":\"tab\\there\"}", "authorizationReference" },
            { "{" + units + ",\"justification\":\"Revisada.\",\"authorizationReference\":\"" + new string('r', 201) + "\"}", "authorizationReference" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task AnInvalidBodyIsABadRequestThatNamesOnlyTheField(string json, string field)
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await browser.PostRawJsonAsync(Path, json, Keyed());

        var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal([field], body["errors"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("0194f1e0", body.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(0, await RunsOfAsync(moderator.UserId));
    }

    [Theory]
    [InlineData("{\"units\":[],\"justification\":\"a\",\"authorizationReference\":\"b\",\"state\":\"Closed\"}")]
    [InlineData("{\"units\":[],\"justification\":\"a\",\"authorizationReference\":\"b\",\"createdByUserId\":\"0194f1e0-0000-7000-8000-000000000001\"}")]
    [InlineData("{\"units\":[],\"justification\":\"a\",\"authorizationReference\":\"b\",\"evidenceId\":\"forjada\"}")]
    [InlineData("{\"units\":\"nao-e-lista\",\"justification\":\"a\",\"authorizationReference\":\"b\"}")]
    [InlineData("{\"units\":[{\"subjectUserId\":\"nao-e-uuid\"}],\"justification\":\"a\",\"authorizationReference\":\"b\"}")]
    [InlineData("{")]
    public async Task AnUnknownFieldOrMalformedJsonIsRefusedBeforeAnyWork(string json)
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await browser.PostRawJsonAsync(Path, json, Keyed());

        await AssertBareProblemAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        Assert.Equal(0, await RunsOfAsync(moderator.UserId));
    }

    [Fact]
    public async Task WithoutAConfiguredCutoverNoDigestMatchesAndNothingIsWritten()
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await browser.PostJsonAsync(Path, Body(), Keyed());

        await AssertBareProblemAsync(response, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        Assert.Equal(0, await RunsOfAsync(moderator.UserId));
    }

    /// <summary>Mesma cota da prévia: a criação recalcula a mesma classificação (ADR-0049, revisão da fatia 2).</summary>
    [Fact]
    public async Task TheCreationSpendsTheQuotaOfThePreview()
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            using var allowed = await browser.PostJsonAsync(Path, Body());
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        using var create = await browser.PostJsonAsync(Path, Body(), Keyed());
        using var preview = await browser.GetAsync(RegistrationReconciliationPreviewHttpTests.Path);

        await AssertBareProblemAsync(create, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.True(ProblemAssert.RetryAfterSeconds(create) is > 0 and <= 60);
        await AssertBareProblemAsync(preview, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.Equal(0, await RunsOfAsync(moderator.UserId));
    }

    /// <summary>JSON malformado e campo desconhecido caem no binding, antes do handler que gasta a cota.</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("{\"units\":[],\"justification\":\"a\",\"authorizationReference\":\"b\",\"state\":\"Closed\"}")]
    public async Task ABodyThatFailsTheBindingSpendsNoQuota(string json)
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            using var refused = await browser.PostRawJsonAsync(Path, json, Keyed());
            await AssertBareProblemAsync(refused, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        }

        using var preview = await browser.GetAsync(RegistrationReconciliationPreviewHttpTests.Path);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    [Fact]
    public async Task ThePreviewsSpendTheQuotaOfTheCreationButNotOfTheRead()
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            using var allowed = await browser.GetAsync(RegistrationReconciliationPreviewHttpTests.Path);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var create = await browser.PostJsonAsync(Path, Body(), Keyed());
        using var read = await browser.GetAsync(RunPath());

        await AssertBareProblemAsync(create, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        await AssertBareProblemAsync(read, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task TheContractDeclaresTheRealLimits()
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(
            System.IO.Path.Combine(RepositoryRoot.Find(), "docs", "api", "openapi.json"), TestContext.Current.CancellationToken))!;
        var schemas = document["components"]!["schemas"]!;
        var request = schemas["CreateRegistrationReconciliationRunRequest"]!["properties"]!;
        var unit = schemas["ReconciliationRunUnitRequest"]!["properties"]!;

        Assert.Equal((1, 10), ((int)request["units"]!["minItems"]!, (int)request["units"]!["maxItems"]!));
        Assert.Equal((1, 2000), ((int)request["justification"]!["minLength"]!, (int)request["justification"]!["maxLength"]!));
        Assert.Equal((1, 200), ((int)request["authorizationReference"]!["minLength"]!, (int)request["authorizationReference"]!["maxLength"]!));
        Assert.NotNull(request["justification"]!["pattern"]);
        Assert.NotNull(request["authorizationReference"]!["pattern"]);
        Assert.Equal("^[0-9a-f]{64}$", (string?)unit["unitDigest"]!["pattern"]);
        Assert.Equal("^[0-9a-f]{64}$", (string?)schemas["ReconciliationPreviewUnitResponse"]!["properties"]!["unitDigest"]!["pattern"]);
        Assert.Equal(["authorizationReference", "justification", "units"],
            schemas["CreateRegistrationReconciliationRunRequest"]!["required"]!.AsArray().Select(name => (string)name!).Order(StringComparer.Ordinal));
    }
}
