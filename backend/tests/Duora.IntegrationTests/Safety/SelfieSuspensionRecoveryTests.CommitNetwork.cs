using System.Net;
using Duora.Core.Safety;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed partial class SelfieSuspensionRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALostCommitConnectionCanBeRetriedWithTheSameKeyWithoutDuplicatingTheHumanMeasure(bool serverCommitted)
    {
        var (user, delivery) = await DeadAsync();
        var (actor, browser) = await _scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        // The session was issued by the real HTTP login/MFA flow; no synthetic session grants permission.
        var session = Guid.Parse(await _scenario.TextAsync(
            "select id::text from identity.login_sessions where user_id=$1 and revoked_at is null", actor.UserId));
        var key = AccountSuspensionScenario.NewKey();
        await using var proxy = new CommitConnectionProxy(_connectionString);
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(proxy.ConnectionString);
        await using (var db = new DuoraDbContext(options.Options))
        {
            var suspensions = new AccountSuspensions(db, _time);
            proxy.Arm(serverCommitted, commitsToSkip: 0);

            await Assert.ThrowsAnyAsync<NpgsqlException>(() => suspensions.SuspendAsModeratorAsync(
                new ModeratorSession(actor.UserId, session),
                SuspensionOrder.ByModerator(actor.UserId, user, SuspensionReason.SelfieVerificationFailed, Reason),
                ModerationCommandKey.ForModerator(actor.UserId, key), Ct, failedDeliveryId: delivery));
            Assert.Equal(serverCommitted, await proxy.WaitForCutAsync(Ct));
            Assert.Equal(0, proxy.ForwardedCommitAcknowledgements);
        }

        // Inspect through an independent connection: losing the acknowledgement does not prove rollback.
        var expected = serverCommitted ? 1 : 0;
        Assert.Equal(serverCommitted ? "Suspended" : "PendingEligibility", await StatusAsync(user));
        Assert.Equal(expected, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id=$1", user));
        Assert.Equal(expected, await RecoveryAuditCountAsync(delivery));
        Assert.Equal(expected, await _scenario.CountAsync("""
            select count(*) from ops.audit_events a join safety.moderation_actions m on m.id=a.target_id
            where m.subject_user_id=$1 and a.action='safety.account_suspended'
            """, user));
        var committedId = serverCommitted
            ? await _scenario.TextAsync("select id::text from safety.moderation_actions where subject_user_id=$1", user)
            : null;

        using var retry = await RecoverAsync(browser, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var recovered = await MfaScenario.ReadObjectAsync(retry);
        var id = (string)recovered["suspensionId"]!;
        if (committedId is not null)
        {
            Assert.Equal(committedId, id);
        }
        Assert.False((bool)recovered["decidedAutomatically"]!);
        Assert.Equal("SelfieVerificationFailed", (string?)recovered["reasonCode"]);
        using var replay = await RecoverAsync(browser, user, delivery, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(id, (string?)(await MfaScenario.ReadObjectAsync(replay))["suspensionId"]);
        Assert.Equal("Suspended", await StatusAsync(user));
        Assert.Equal(1, await _scenario.CountAsync("""
            select count(*) from safety.moderation_actions where subject_user_id=$1
              and id=$2 and author_kind='Human' and author_user_id=$3
              and reason_code='SelfieVerificationFailed' and internal_reason=$4
            """, user, Guid.Parse(id), actor.UserId, Reason));
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id=$1", user));
        Assert.Equal(1, await RecoveryAuditCountAsync(delivery));
        Assert.Equal(1, await _scenario.CountAsync("""
            select count(*) from ops.audit_events where target_id=$1 and action='safety.account_suspended'
              and actor_kind='Moderator' and actor_id=$2
            """, Guid.Parse(id), actor.UserId));
        Assert.Equal(1, await _scenario.CountAsync("""
            select count(*) from ops.outbox_deliveries d join ops.outbox_events e on e.id=d.outbox_event_id
            where d.id=$1 and d.status='Failed' and d.attempts=10 and d.reviewed_at is null and e.payload='{}'::jsonb
            """, delivery));
    }
}
