using System.Net;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Operations;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class SelfieSuspensionRecoveryTests
{
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized, "UNAUTHENTICATED")]
    [InlineData("participant", HttpStatusCode.Forbidden, "FORBIDDEN")]
    [InlineData("moderator-without-mfa", HttpStatusCode.Forbidden, "MFA_ENROLLMENT_REQUIRED")]
    public async Task ASessionMustBeAdministrativeAndHaveProvedMfa(string kind, HttpStatusCode status, string code)
    {
        var (user, delivery) = await DeadAsync();
        using var browser = kind switch
        {
            "anonymous" => new Browser(_factory),
            "participant" => await _scenario.Identity.SignedInBrowserAsync(await _scenario.Identity.CreateConfirmedAccountAsync()),
            _ => await _scenario.Identity.SignedInBrowserAsync(await _scenario.Identity.CreatePrivilegedAccountAsync(AccountRoles.Moderator)),
        };
        using var response = await RecoverAsync(browser, user, delivery);
        await ProblemAssert.IsProblemAsync(response, status, code);
        await AssertNoMeasureAsync(user);
        await AssertNoRecoveryAuditAsync(delivery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CsrfAndOriginChecksProtectTheRecovery(bool crossOrigin)
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        using var response = await browser.PostJsonAsync(Path(user, delivery), new { internalReason = Reason },
            new MutationHeaders(SendCsrfToken: crossOrigin, Origin: crossOrigin ? "https://untrusted.invalid" : DuoraWebFactory.PublicOrigin,
                Extra: new Dictionary<string, string> { ["Idempotency-Key"] = AccountSuspensionScenario.NewKey() }));
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "CSRF_VALIDATION_FAILED");
        await AssertNoMeasureAsync(user);
        await AssertNoRecoveryAuditAsync(delivery);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"internalReason\":\"\"}")]
    [InlineData("{\"internalReason\":\"  \"}")]
    [InlineData("{\"internalReason\":\"incidente\",\"moderatorId\":\"fabricado\"}")]
    public async Task InvalidOrExtendedBodiesCannotApplyASuspension(string json)
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        using var response = await browser.PostRawJsonAsync(Path(user, delivery), json,
            AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoMeasureAsync(user);
        await AssertNoRecoveryAuditAsync(delivery);
    }

    [Fact]
    public async Task AStableKeyIsRequiredAndTheOrdinaryRouteStillRefusesTheSelfieReason()
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        using var missing = await browser.PostJsonAsync(Path(user, delivery), new { internalReason = Reason });
        using var invalid = await RecoverAsync(browser, user, delivery, "bad key");
        using var ordinary = await browser.PostJsonAsync(AccountSuspensionScenario.SuspendPath(user),
            new { reasonCode = "SelfieVerificationFailed", internalReason = Reason },
            AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));
        await ProblemAssert.IsProblemAsync(missing, HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_INVALID");
        await ProblemAssert.IsProblemAsync(invalid, HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_INVALID");
        await ProblemAssert.IsProblemAsync(ordinary, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertNoMeasureAsync(user);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheDomainBoundaryRejectsAnInventedOrRevokedSession(bool revoked)
    {
        var (user, delivery) = await DeadAsync();
        var (actor, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var session = Guid.NewGuid();
        if (revoked)
        {
            session = Guid.Parse(await _scenario.TextAsync("select id::text from identity.login_sessions where user_id=$1 and revoked_at is null", actor.UserId));
            await _scenario.ExecuteAsync("update identity.login_sessions set revoked_at=$2 where id=$1", session, _time.GetUtcNow());
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountSuspensions>();
        var outcome = await service.SuspendAsModeratorAsync(new ModeratorSession(actor.UserId, session),
            SuspensionOrder.ByModerator(actor.UserId, user, SuspensionReason.SelfieVerificationFailed, Reason),
            ModerationCommandKey.ForModerator(actor.UserId, AccountSuspensionScenario.NewKey()), Ct, failedDeliveryId: delivery);

        Assert.IsType<SuspensionOutcome.Forbidden>(outcome);
        await AssertNoMeasureAsync(user);
        await AssertNoRecoveryAuditAsync(delivery);
    }

    [Fact]
    public async Task RecoveryAndDeliveryReviewAreSeparateAuditedOperationsAndAReplayRemainsSafeAfterReview()
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var key = AccountSuspensionScenario.NewKey();
        using var applied = await RecoverAsync(browser, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        await using var source = NpgsqlDataSource.Create(_connectionString);
        var retention = new RetentionService(source, _time);
        Assert.True(OperatorName.TryParse("synthetic-reviewer", out var actor));
        Assert.True(await retention.ReviewFailureAsync(delivery, actor, FailureResolution.Resolved, Ct));
        Assert.False(await retention.ReviewFailureAsync(delivery, actor, FailureResolution.Resolved, Ct));
        using var replay = await RecoverAsync(browser, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(1, await _scenario.CountAsync("select count(*) from ops.outbox_deliveries where id=$1 and status='Failed' and reviewed_at is not null", delivery));
        Assert.Equal(1, await _scenario.CountAsync("select count(*) from ops.audit_events where target_id=$1 and action='ops.outbox_failure_resolved'", delivery));
        Assert.Equal(1, await RecoveryAuditCountAsync(delivery));
    }
}
