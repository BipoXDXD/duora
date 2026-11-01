using System.Net;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Safety;

public sealed partial class SelfieSuspensionRecoveryTests
{
    [Theory]
    [InlineData(AccountRoles.ProductAdmin)]
    [InlineData(AccountRoles.Operator)]
    public async Task OtherAdministrativeRolesCannotDiagnoseOrRecover(string role)
    {
        await using var h = await Harness.Create(postgres);
        var (_, browser) = await h.Scenario.PrivilegedBrowserAsync(role);
        using var _ = browser;
        using var read = await browser.GetAsync(PathFor(h.Delivery));
        using var write = await browser.PostJsonAsync(PathFor(h.Delivery), Body(), AccountSuspensionScenario.WithKey("wrong-role"));
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        await AssertUntouched(h);
    }

    [Fact]
    public async Task AnonymousAndNormalSessionsCannotUseTheTool()
    {
        await using var h = await Harness.Create(postgres);
        using var anonymous = new Browser(h.Factory);
        using var read = await anonymous.GetAsync(PathFor(h.Delivery));
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        var account = await h.Scenario.Identity.CreateConfirmedAccountAsync();
        using var normal = await h.Scenario.Identity.SignedInBrowserAsync(account);
        using var write = await normal.PostJsonAsync(PathFor(h.Delivery), Body(), AccountSuspensionScenario.WithKey("normal-session"));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        await AssertUntouched(h);
    }

    [Theory]
    [InlineData("revoked", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("stamp", HttpStatusCode.Unauthorized)]
    [InlineData("role", HttpStatusCode.Forbidden)]
    [InlineData("mfa", HttpStatusCode.Forbidden)]
    [InlineData("session-mfa", HttpStatusCode.Forbidden)]
    [InlineData("normal-policy", HttpStatusCode.Forbidden)]
    [InlineData("unconfirmed", HttpStatusCode.Forbidden)]
    [InlineData("inactive", HttpStatusCode.Forbidden)]
    [InlineData("absolute-expired", HttpStatusCode.Unauthorized)]
    public async Task LiveSessionAndAccountAuthorizationAreRequired(string variant, HttpStatusCode expected)
    {
        await using var h = await Harness.Create(postgres);
        var (actor, moderator) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        switch (variant)
        {
            case "unconfirmed":
                await h.Sql("update identity.users set email_confirmed=false where id=$1", actor.UserId);
                break;
            case "inactive":
                await h.Sql("update identity.users set status='Restricted' where id=$1", actor.UserId);
                break;
            case "absolute-expired":
                await h.Sql("update identity.login_sessions set idle_expires_at=$2,absolute_expires_at=$2 where user_id=$1", actor.UserId, h.Time.GetUtcNow());
                break;
            case "revoked":
                await h.Sql("update identity.login_sessions set revoked_at=$2 where user_id=$1", actor.UserId, h.Time.GetUtcNow());
                break;
            case "expired":
                await h.Sql("update identity.login_sessions set idle_expires_at=$2 where user_id=$1", actor.UserId, h.Time.GetUtcNow());
                break;
            case "stamp":
                await h.Sql("update identity.users set security_stamp='changed' where id=$1", actor.UserId);
                break;
            case "role":
                await h.Sql("delete from identity.user_roles where user_id=$1", actor.UserId);
                break;
            case "session-mfa":
                await h.Sql("update identity.login_sessions set mfa_verified_at=null,totp_verified_at=null where user_id=$1", actor.UserId);
                break;
            case "mfa":
                await h.Sql("update identity.users set two_factor_enabled=false where id=$1", actor.UserId);
                break;
            case "normal-policy":
                await h.Sql("update identity.login_sessions set policy='Normal' where user_id=$1", actor.UserId);
                break;
        }
        using var response = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(), AccountSuspensionScenario.WithKey("stale-proof"));
        Assert.Equal(expected, response.StatusCode);
        await AssertUntouched(h);
    }

    [Theory]
    [InlineData(-3600, true)]
    [InlineData(-3601, false)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    public async Task TotpWindowIsCheckedByTheServerWithoutExtendingTheSession(int? seconds, bool allowed)
    {
        await using var h = await Harness.Create(postgres);
        var (actor, moderator) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        if (seconds is { } offset)
        {
            await h.Sql("update identity.login_sessions set totp_verified_at=$2 where user_id=$1", actor.UserId, h.Time.GetUtcNow().AddSeconds(offset));
        }
        else
        {
            await h.Sql("update identity.login_sessions set totp_verified_at=null where user_id=$1", actor.UserId);
        }
        using var response = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(), AccountSuspensionScenario.WithKey("totp-window"));
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        if (!allowed)
        {
            await AssertUntouched(h);
        }
    }

    [Fact]
    public async Task CsrfAndCallerSuppliedActorAreRejectedWithoutEffects()
    {
        await using var h = await Harness.Create(postgres);
        var (_, moderator) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        using var noCsrf = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(),
            AccountSuspensionScenario.WithKey("no-csrf") with { SendCsrfToken = false });
        await ProblemAssert.IsProblemAsync(noCsrf, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        using var foreignOrigin = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(),
            AccountSuspensionScenario.WithKey("foreign-origin") with { Origin = "https://untrusted.example", FetchSite = "cross-site" });
        await ProblemAssert.IsProblemAsync(foreignOrigin, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        using var spoof = await moderator.PostJsonAsync(PathFor(h.Delivery), new
        {
            incidentReference = "INC-319",
            internalReason = "Sintético",
            moderatorId = Guid.NewGuid(),
        }, AccountSuspensionScenario.WithKey("spoof"));
        Assert.Equal(HttpStatusCode.BadRequest, spoof.StatusCode);
        await AssertUntouched(h);
    }

    [Fact]
    public async Task ReplayAfterPermissionLossCannotReadTheReceiptOrCreateEffects()
    {
        await using var h = await Harness.Create(postgres);
        var (actor, moderator) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        var key = AccountSuspensionScenario.WithKey("auth-replay");
        using var first = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(), key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await h.Sql("delete from identity.user_roles where user_id=$1", actor.UserId);
        using var replay = await moderator.PostJsonAsync(PathFor(h.Delivery), Body(), key);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(1, await h.Count("select count(*) from safety.moderation_actions where subject_user_id=$1", h.User));
        Assert.Equal(1, await h.Count("select count(*) from safety.selfie_suspension_recoveries"));
    }

    [Fact]
    public async Task ARealRecoveryCodeSessionRequiresTotpAndDoesNotRenewItsProof()
    {
        await using var h = await Harness.Create(postgres);
        var actor = await h.Scenario.Identity.CreatePrivilegedAccountAsync(AccountRoles.Moderator);
        await h.Sql("update identity.users set status='Active' where id=$1", actor.UserId);
        using var enrollment = await h.Scenario.Identity.SignedInBrowserAsync(actor);
        var mfa = await MfaScenario.EnableAsync(enrollment, h.Time);
        using var browser = new Browser(h.Factory);
        using var login = await MfaScenario.LoginAsync(browser, actor, recoveryCode: mfa.RecoveryCodes[0]);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        await browser.FetchPrivateContextAsync();
        var session = await MfaScenario.ReadSessionIdAsync(browser);
        var expires = await h.Text("select absolute_expires_at::text from identity.login_sessions where id=$1", session);

        using var response = await browser.PostJsonAsync(PathFor(h.Delivery), Body(), AccountSuspensionScenario.WithKey("recovery-code"));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED");
        Assert.Equal(1, await h.Count("select count(*) from identity.login_sessions where id=$1 and mfa_verified_at is not null and totp_verified_at is null", session));
        Assert.Equal(expires, await h.Text("select absolute_expires_at::text from identity.login_sessions where id=$1", session));
        await AssertUntouched(h);
    }

    private static async Task AssertUntouched(Harness h)
    {
        Assert.Equal("PendingEligibility", await h.Text("select status from identity.users where id=$1", h.User));
        Assert.Equal(10, await h.Count("select count(*) from identity.selfie_failures where user_id=$1", h.User));
        Assert.Equal(0, await h.Count("select count(*) from safety.moderation_actions where subject_user_id=$1", h.User));
        Assert.Equal(1, await h.Count("select count(*) from ops.outbox_deliveries where id=$1 and reviewed_at is null", h.Delivery));
    }
}
