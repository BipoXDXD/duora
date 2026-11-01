using System.Net;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Safety;

public sealed partial class SelfieSuspensionRecoveryTests
{
    [Theory]
    [InlineData("update ops.outbox_deliveries set consumer='other' where id=$1")]
    [InlineData("update ops.outbox_deliveries set status='Delivered' where id=$1")]
    [InlineData("update ops.outbox_deliveries set status='Pending',completed_at=null where id=$1")]
    [InlineData("update ops.outbox_deliveries set reviewed_at=now() where id=$1")]
    [InlineData("update ops.outbox_events set type='other' where id=(select outbox_event_id from ops.outbox_deliveries where id=$1)")]
    [InlineData("update ops.outbox_events set schema_version=2 where id=(select outbox_event_id from ops.outbox_deliveries where id=$1)")]
    [InlineData("update ops.outbox_events set resource_type='other' where id=(select outbox_event_id from ops.outbox_deliveries where id=$1)")]
    [InlineData("update ops.outbox_events set resource_id=gen_random_uuid() where id=(select outbox_event_id from ops.outbox_deliveries where id=$1)")]
    public async Task AnUnrelatedNonFailedReviewedOrUnboundDeliveryCannotAuthorizeASuspension(string mutation)
    {
        var (user, delivery) = await DeadAsync();
        await _scenario.ExecuteAsync(mutation, delivery);
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await RecoverAsync(browser, user, delivery);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "SELFIE_RECOVERY_UNAVAILABLE");
        await AssertNoMeasureAsync(user);
    }

    [Fact]
    public async Task ADeadDeliveryCannotBeUsedToSuspendAnotherAccount()
    {
        var (_, delivery) = await DeadAsync();
        var (other, _) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await RecoverAsync(browser, other, delivery);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "SELFIE_RECOVERY_UNAVAILABLE");
        await AssertNoMeasureAsync(other);
    }

    [Fact]
    public async Task ClearingTheLedgerForAnApprovalMakesTheOldRequestUnavailable()
    {
        var (user, delivery) = await DeadAsync();
        await _scenario.ExecuteAsync("delete from identity.selfie_failures where user_id=$1", user);
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await RecoverAsync(browser, user, delivery);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "SELFIE_RECOVERY_UNAVAILABLE");
        await AssertNoMeasureAsync(user);
    }

    [Fact]
    public async Task ABoundRequestWhoseCurrentLedgerIsNoLongerExhaustedDoesNotSuspend()
    {
        var (user, delivery) = await DeadAsync();
        await _scenario.ExecuteAsync("""
            delete from identity.selfie_failures where user_id=$1 and verification_id not in
              (select e.resource_id from ops.outbox_events e join ops.outbox_deliveries d on d.outbox_event_id=e.id where d.id=$2)
            """, user, delivery);
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        using var response = await RecoverAsync(browser, user, delivery);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "SELFIE_RECOVERY_NOT_REQUIRED");
        await AssertNoMeasureAsync(user);
    }

    [Fact]
    public async Task AnotherActiveMeasureKeepsTheRecoveryPendingUntilThatMeasureIsLifted()
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var terms = await AccountSuspensionScenario.SuspendAsync(browser, user);
        var key = AccountSuspensionScenario.NewKey();

        using var blocked = await RecoverAsync(browser, user, delivery, key);

        await ProblemAssert.IsProblemAsync(blocked, HttpStatusCode.Conflict, "ACCOUNT_ALREADY_SUSPENDED");
        Assert.Equal(1, await _scenario.CountAsync("select count(*) from safety.moderation_actions where subject_user_id=$1", user));
        using var lifted = await browser.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(user, Guid.Parse((string)terms["suspensionId"]!)),
            new { internalReason = "Revogada a outra medida." },
            AccountSuspensionScenario.WithIfMatch((string)terms["eTag"]!));
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        using var recovered = await RecoverAsync(browser, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal("SelfieVerificationFailed", (string?)(await MfaScenario.ReadObjectAsync(recovered))["reasonCode"]);
    }

    [Fact]
    public async Task AReplayAfterALiftReturnsTheLiftedMeasureWithoutSuspendingAgainEvenWithANewKey()
    {
        var (user, delivery) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var key = AccountSuspensionScenario.NewKey();
        using var first = await RecoverAsync(browser, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await MfaScenario.ReadObjectAsync(first);
        var id = Guid.Parse((string)original["suspensionId"]!);
        using var lifted = await browser.PostJsonAsync(AccountSuspensionScenario.LiftPath(user, id),
            new { internalReason = "Revisão humana da selfie." },
            AccountSuspensionScenario.WithIfMatch((string)original["eTag"]!));
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        using var replay = await RecoverAsync(browser, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var repeated = await MfaScenario.ReadObjectAsync(replay);
        Assert.Equal((string?)original["suspensionId"], (string?)repeated["suspensionId"]);
        Assert.NotNull(repeated["liftedAt"]);
        using var newKey = await RecoverAsync(browser, user, delivery);
        await ProblemAssert.IsProblemAsync(newKey, HttpStatusCode.Conflict, "SELFIE_RECOVERY_UNAVAILABLE");
        Assert.Equal("PendingEligibility", await StatusAsync(user));
        Assert.Equal(1, await _scenario.CountAsync("select count(*) from safety.moderation_actions where subject_user_id=$1", user));
        Assert.Equal(1, await _scenario.CountAsync("select count(*) from ops.audit_events where action='safety.account_suspended' and target_id=$1", id));
    }

    [Fact]
    public async Task AKeyIsBoundToTheDeliveryTheAccountAndTheReasonAndAnEqualReplayDoesNotDuplicateAudit()
    {
        var (user, delivery) = await DeadAsync();
        var (other, second) = await DeadAsync();
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var key = AccountSuspensionScenario.NewKey();
        using var first = await RecoverAsync(browser, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await MfaScenario.ReadObjectAsync(first);
        using var same = await RecoverAsync(browser, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, same.StatusCode);
        Assert.Equal((string?)original["suspensionId"], (string?)(await MfaScenario.ReadObjectAsync(same))["suspensionId"]);
        using var changedReason = await RecoverAsync(browser, user, delivery, key, "Outro incidente.");
        using var changedDelivery = await RecoverAsync(browser, user, second, key);
        using var changedAccount = await RecoverAsync(browser, other, second, key);
        await ProblemAssert.IsProblemAsync(changedReason, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        await ProblemAssert.IsProblemAsync(changedDelivery, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        await ProblemAssert.IsProblemAsync(changedAccount, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        Assert.Equal(1, await _scenario.CountAsync("select count(*) from ops.audit_events where action='safety.account_suspension_source_recorded' and target_id=$1", delivery));
        await AssertNoMeasureAsync(other);
    }

    [Theory]
    [InlineData("PendingEmail")]
    [InlineData("DeletionPending")]
    public async Task ARequestForAnIneligibleAccountDoesNotApplyAMeasure(string status)
    {
        var (user, delivery) = await DeadAsync();
        await _scenario.ExecuteAsync("update identity.users set status=$2 where id=$1", user, status);
        var (_, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        using var response = await RecoverAsync(browser, user, delivery);
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "ACCOUNT_NOT_SUSPENDABLE");
        Assert.Equal(status, await StatusAsync(user));
        Assert.Equal(0, await _scenario.CountAsync("select count(*) from safety.moderation_actions where subject_user_id=$1", user));
    }
}
