using System.Net;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// API-ADM25 (issue #321): prévia da reconciliação de inscrições legadas pela API, com cookie real. Autorização de
/// sessão administrativa, papel <c>moderator</c> lido ao vivo e TOTP recente; cursor só com os dois campos juntos.
/// Sem descritor de corte configurado, nada é candidato.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class RegistrationReconciliationPreviewHttpTests : IAsyncDisposable
{
    public const string Path = "/api/v1/admin/registration-reconciliations/preview";

    /// <summary>Campos de um Problem Details; um erro desta rota não carrega mais nada.</summary>
    private static readonly string[] ProblemKeys = ["code", "status", "title", "traceId", "type"];

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly AccountSuspensionScenario _scenario;

    public RegistrationReconciliationPreviewHttpTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
        _scenario = new AccountSuspensionScenario(postgres.ConnectionString, _factory, _time);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task WithoutASessionThePreviewIsUnauthorizedAndCarriesNoData()
    {
        using var browser = new Browser(_factory);

        using var response = await browser.GetWithoutContextAsync(Path);

        await AssertBareProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task AParticipantIsForbidden()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        using var browser = await _scenario.Identity.SignedInBrowserAsync(participant);

        using var response = await browser.GetAsync(Path);

        await AssertBareProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Theory]
    [InlineData(AccountRoles.Operator)]
    [InlineData(AccountRoles.ProductAdmin)]
    public async Task AnAdministrativeSessionWithoutTheModeratorRoleIsForbidden(string role)
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync(role);
        using var _ = browser;

        using var response = await browser.GetAsync(Path);

        await AssertBareProblemAsync(response, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
    }

    [Fact]
    public async Task TheModeratorRoleIsReadLiveOnEveryPreview()
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        using (var allowed = await browser.GetAsync(Path))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        await _scenario.ExecuteAsync(
            "delete from identity.user_roles where user_id = $1 and role_id = (select id from identity.roles where name = $2)",
            moderator.UserId, AccountRoles.Moderator);
        await _scenario.Identity.GrantRoleAsync(moderator.UserId, AccountRoles.ProductAdmin);
        using var revoked = await browser.GetAsync(Path);

        await AssertBareProblemAsync(revoked, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
    }

    [Fact]
    public async Task AnExpiredTotpProofRequiresTheExistingStepUp()
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await StepUpClock.ExpireRecentAuthenticationAsync(browser, _time);

        using var response = await browser.GetAsync(Path);

        await AssertBareProblemAsync(response, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
    }

    [Fact]
    public async Task ATotpProofInTheFutureIsNotRecent()
    {
        var (moderator, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        await _scenario.ExecuteAsync(
            "update identity.login_sessions set totp_verified_at = $2 where user_id = $1 and revoked_at is null",
            moderator.UserId, _time.GetUtcNow().AddMinutes(1));

        using var response = await browser.GetAsync(Path);

        await AssertBareProblemAsync(response, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
    }

    [Fact]
    public async Task WithoutThePrivateProofThePreviewIsRefused()
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await browser.GetAsync(Path, privateProofOverride: "forjado");

        await AssertBareProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
    }

    [Fact]
    public async Task TheModeratorReceivesOnlyTheAllowlistedFieldsAndNoCutoverMeansNoCandidate()
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await browser.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var body = await MfaScenario.ReadObjectAsync(response);
        Assert.Equal(
            ["actionableRegistrations", "asOf", "evidenceId", "heldRegistrations", "next", "page",
                "registrationsByPrimaryIssue", "selectorVersion", "units"],
            body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Null(body["evidenceId"]);
        Assert.Equal(1, (int)body["selectorVersion"]!);
        Assert.Equal(0, (int)body["actionableRegistrations"]!);
        Assert.Equal(0, (int)body["heldRegistrations"]!);
        Assert.Empty(body["page"]!.AsArray());
        Assert.Null(body["next"]);
        Assert.Equal(_time.GetUtcNow(), DateTimeOffset.Parse((string)body["asOf"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith("Z", (string)body["asOf"]!, StringComparison.Ordinal);
        var units = body["units"]!.AsObject();
        Assert.Equal(["blocked", "eligible", "oversized"], units.Select(p => p.Key).Order(StringComparer.Ordinal));
        // O banco é compartilhado: unidades pausadas por integridade podem existir; candidatas, sem corte, nunca.
        Assert.Equal((0, 0), ((int)units["eligible"]!, (int)units["oversized"]!));
        foreach (var entry in body["registrationsByPrimaryIssue"]!.AsArray())
        {
            Assert.Equal(["issue", "registrations"], entry!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
            Assert.NotEqual("Candidate", (string?)entry["issue"]);
        }
    }

    [Fact]
    public async Task ACursorWithBothFieldsIsAccepted()
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await browser.GetAsync($"{Path}?cursorSubject={Guid.NewGuid()}&cursorMeasure={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("?cursorSubject=00000000-0000-0000-0000-00000000000a")]
    [InlineData("?cursorMeasure=00000000-0000-0000-0000-00000000000a")]
    [InlineData("?cursorSubject=nao-e-uuid&cursorMeasure=00000000-0000-0000-0000-00000000000a")]
    [InlineData("?cursorSubject=00000000-0000-0000-0000-00000000000a&cursorMeasure=")]
    [InlineData("?cursorSubject=&cursorMeasure=")]
    [InlineData("?cursorSubject=00000000000000000000000000000000a&cursorMeasure=00000000-0000-0000-0000-00000000000a")]
    [InlineData("?cursorSubject=0000000000000000000000000000000a&cursorMeasure=00000000-0000-0000-0000-00000000000a")]
    [InlineData("?cursorSubject=00000000-0000-0000-0000-000000000000&cursorMeasure=00000000-0000-0000-0000-00000000000a")]
    [InlineData("?cursorSubject=00000000-0000-0000-0000-00000000000a&cursorMeasure=00000000-0000-0000-0000-00000000000b&cursorMeasure=00000000-0000-0000-0000-00000000000c")]
    [InlineData("?cursorSubject=%00&cursorMeasure=00000000-0000-0000-0000-00000000000a")]
    [InlineData("?cursorSubject=%7B00000000-0000-0000-0000-00000000000a%7D&cursorMeasure=00000000-0000-0000-0000-00000000000a")]
    public async Task AnInvalidOrIncompleteCursorIsABadRequest(string query)
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await browser.GetAsync(Path + query);

        var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(["cursor"], body["errors"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("00000000-0000-0000-0000-00000000000a", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACursorIsCheckedOnlyAfterTheAuthorization()
    {
        var (_, browser) = await _scenario.PrivilegedBrowserAsync(AccountRoles.Operator);
        using var _ = browser;

        using var response = await browser.GetAsync($"{Path}?cursorSubject=nao-e-uuid");

        await AssertBareProblemAsync(response, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN");
    }

    private static async Task AssertBareProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await ProblemAssert.IsProblemAsync(response, status, code);
        Assert.Equal(ProblemKeys, body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}
