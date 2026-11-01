using System.Net;
using Duora.Core.Safety;
using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Duora.IntegrationTests.Participation;

namespace Duora.IntegrationTests.Safety;

public sealed partial class SelfieSuspensionRecoveryTests
{
    [Fact]
    public async Task ExistingSelfieMeasureIsConfirmedWithoutAnotherDecision()
    {
        await using var h = await Harness.Create(postgres);
        await using (var db = PostgresFixture.CreateDbContext(h.ConnectionString))
        {
            var suspension = await new AccountSuspensions(db, h.Time).SuspendAutomaticallyAsync(
                SuspensionOrder.Automated(h.User, SuspensionReason.SelfieVerificationFailed, "Limite sintético."),
                ModerationCommandKey.ForSystem("selfie-verification", h.Verification), Ct);
            Assert.IsType<SuspensionOutcome.Suspended>(suspension);
        }
        var (actor, browser) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = browser;

        var outcome = await Recover(h, actor.UserId, "existing-selfie");

        Assert.Equal("ExistingSuspension", outcome.Result!.Disposition);
        Assert.Equal(1, await h.Count("select count(*) from safety.moderation_actions where subject_user_id=$1", h.User));
        Assert.Equal(1, await h.Count("select count(*) from ops.audit_events where action='ops.outbox_failure_resolved' and actor_id=$1", actor.UserId));
        Assert.Equal(1, await h.Count("select count(*) from ops.outbox_deliveries where id=$1 and reviewed_at is not null", h.Delivery));
    }

    [Fact]
    public async Task RecoveryWithdrawsRegistrationAbortsTheRoomAndInvalidatesOpenAccess()
    {
        await using var h = await Harness.Create(postgres);
        var seed = await BrowserScenarioSeed.InitializeAsync(h.ConnectionString, h.Time.GetUtcNow(), Ct,
            Guid.NewGuid().ToString("N"), declarePreferences: true);
        await h.UseSubject(seed.Bob);
        await using var source = Npgsql.NpgsqlDataSource.Create(h.ConnectionString);
        var participation = new ParticipationService(source, h.Time);
        foreach (var user in new[] { seed.Alice, seed.Bob })
        {
            Assert.Equal(ParticipationOutcome.Done, await participation.RegisterAsync(seed.Events[0], user, false, Ct));
            Assert.Equal(ParticipationOutcome.Done, await participation.RegisterAsync(seed.Events[0], user, true, Ct));
        }
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await participation.BuildRoundPairingsAsync(seed.Rounds[0], Ct);
        var room = (await participation.StatusAsync(seed.Events[0], seed.Bob, Ct))!.RoomId!.Value;
        await participation.RespondAsync(room, seed.Alice, true, Ct);
        await participation.RespondAsync(room, seed.Bob, true, Ct);
        using var subject = await h.Scenario.Identity.SignedInBrowserAsync(new(seed.Bob, await h.Text("select email from identity.users where id=$1", seed.Bob), IdentityScenario.ValidPassword));
        var version = await h.Count("select security_version from safety.user_guards where user_id=$1", h.User);
        var (actor, moderator) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = moderator;

        Assert.Equal("Suspended", (await Recover(h, actor.UserId, "effects")).Result!.Disposition);

        Assert.Equal("Withdrawn", await h.Text("select state from events.registrations where event_id=$1 and user_id=$2 and checked_in_at is not null", seed.Events[0], h.User));
        Assert.Equal(1, await h.Count("select confirmed_count from events.events where id=$1", seed.Events[0]));
        Assert.Equal("Aborted", await h.Text("select state from experiences.game_sessions where id=$1", room));
        Assert.Equal(0, await h.Count("select count(*) from events.pairing_participants where pairing_id=(select pairing_id from experiences.game_sessions where id=$1) and released_at is null", room));
        Assert.True(await h.Count("select security_version from safety.user_guards where user_id=$1", h.User) > version);
        using var read = await subject.GetAsync($"/api/v1/rooms/{room:D}");
        await ProblemAssert.IsProblemAsync(read, HttpStatusCode.Forbidden, "ACCOUNT_SUSPENDED");
    }

    [Fact]
    public async Task TheApplicationCannotRewriteOrDeleteTheDurableReceipt()
    {
        await using var h = await Harness.Create(postgres);
        var (actor, browser) = await h.Scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        Assert.Equal("Resolved", (await Recover(h, actor.UserId, "immutable")).Code);
        foreach (var sql in new[]
        {
            "update safety.selfie_suspension_recoveries set incident_reference='OTHER'",
            "delete from safety.selfie_suspension_recoveries",
            "truncate safety.selfie_suspension_recoveries",
        })
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => h.Sql(sql));
            Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        Assert.Equal(1, await h.Count("select count(*) from safety.selfie_suspension_recoveries where incident_reference='INC-319'"));
    }
}
