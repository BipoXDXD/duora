using System.Net;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// T-27 (INV-03, INV-06) na recuperação humana da selfie: a mesma <c>Idempotency-Key</c> e o mesmo corpo, depois de o
/// moderador perder o papel ou o TOTP recente, não devolvem a medida guardada nem gravam outra.
/// </summary>
public sealed partial class SelfieSuspensionRecoveryTests
{
    public enum LostAuthorization
    {
        RoleRemoved,
        TotpNoLongerRecent,
    }

    [Theory]
    [Trait("Acceptance", "T-27")]
    [InlineData(LostAuthorization.RoleRemoved, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN")]
    [InlineData(LostAuthorization.TotpNoLongerRecent, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED")]
    public async Task TheSameRecoveryKeyAfterLosingAuthorizationIsRefusedWithoutTheStoredMeasure(
        LostAuthorization loss, HttpStatusCode expectedStatus, string expectedCode)
    {
        var (user, delivery) = await DeadAsync();
        var (account, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        var key = AccountSuspensionScenario.NewKey();
        using var first = await RecoverAsync(moderator, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var measure = (string)(await MfaScenario.ReadObjectAsync(first))["suspensionId"]!;
        await LoseAuthorizationAsync(loss, account, moderator);

        using var replay = await RecoverAsync(moderator, user, delivery, key);

        var body = (await ProblemAssert.IsProblemAsync(replay, expectedStatus, expectedCode)).ToJsonString();
        ReplayLeakAssert.ContainsNone(replay, body, Guid.Parse(measure), user, delivery);
        Assert.False(replay.Headers.Contains("Location"));
        Assert.Null(replay.Headers.ETag);
        await AssertSingleRecoveryAsync(user, delivery, Guid.Parse(measure));
    }

    /// <summary>
    /// Sem a policy da fronteira, o serviço ainda confere o papel antes de procurar a chave: a recuperação repetida por um
    /// ex-moderador dá <c>Forbidden</c>, nunca o <c>Suspended</c> do replay.
    /// </summary>
    [Fact]
    [Trait("Acceptance", "T-27")]
    public async Task TheServiceRefusesTheRecoveryReplayOfAFormerModeratorBeforeLookingUpTheKey()
    {
        var (user, delivery) = await DeadAsync();
        var (account, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var _ = moderator;
        var key = AccountSuspensionScenario.NewKey();
        using var first = await RecoverAsync(moderator, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var measure = Guid.Parse((string)(await MfaScenario.ReadObjectAsync(first))["suspensionId"]!);
        var session = new ModeratorSession(account.UserId, await MfaScenario.ReadSessionIdAsync(moderator));
        await LoseAuthorizationAsync(LostAuthorization.RoleRemoved, account, moderator);

        await using var scope = _factory.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<AccountSuspensions>().SuspendAsModeratorAsync(session,
            SuspensionOrder.ByModerator(account.UserId, user, SuspensionReason.SelfieVerificationFailed, Reason),
            ModerationCommandKey.ForModerator(account.UserId, key), Ct, failedDeliveryId: delivery);

        Assert.IsType<SuspensionOutcome.Forbidden>(outcome);
        await AssertSingleRecoveryAsync(user, delivery, measure);
    }

    private async Task LoseAuthorizationAsync(LostAuthorization loss, TestAccount account, Browser moderator)
    {
        switch (loss)
        {
            case LostAuthorization.RoleRemoved:
                await _scenario.ExecuteAsync("delete from identity.user_roles where user_id = $1", account.UserId);
                await _scenario.Identity.GrantRoleAsync(account.UserId, AccountRoles.ProductAdmin);
                break;
            case LostAuthorization.TotpNoLongerRecent:
                await StepUpClock.ExpireRecentAuthenticationAsync(moderator, _time);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(loss), loss, "Perda de autorização sem cenário.");
        }
    }

    private async Task AssertSingleRecoveryAsync(Guid user, Guid delivery, Guid measure)
    {
        Assert.Equal(1, await _scenario.CountAsync("select count(*) from safety.moderation_actions where subject_user_id=$1", user));
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from ops.audit_events where action='safety.account_suspended' and target_id=$1", measure));
        Assert.Equal(1, await _scenario.CountAsync("""
            select count(*) from ops.audit_events where action='safety.account_suspension_source_recorded'
              and target_type='outbox_delivery' and target_id=$1
            """, delivery));
    }
}
