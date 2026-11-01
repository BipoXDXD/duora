using System.Net;
using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;

namespace Duora.IntegrationTests.Safety;

public sealed class AccountSuspensionRegistrationTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspensionWithdrawsTheRegistrationPreservesHistoryAndPromotesOnce(bool checkedIn)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.RegisterAsync(s.Seed.Third);
        if (checkedIn)
        {
            await s.RegisterAsync(s.Seed.Alice, checkIn: true);
        }
        var commandId = Guid.NewGuid();
        var result = Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice, commandId));

        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Third));
        await s.AssertCapacityAsync(1);
        Assert.Equal(3, await s.CountAsync("select count(*) from events.registrations where event_id=$1", s.EventId));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from events.registrations where event_id=$1 and user_id=$2
              and registered_at=$3 and withdrawn_at=$3 and withdrawn_by_suspension_id=$4
              and withdrawn_from_state=$5 and waitlist_position is null
            """, s.EventId, s.Seed.Alice, s.Clock.GetUtcNow(), result.View.Id, checkedIn ? "CheckedIn" : "Confirmed"));
        Assert.Equal(checkedIn ? 1 : 0, await s.CountAsync("""
            select count(*) from events.registrations where event_id=$1 and user_id=$2 and checked_in_at=$3
            """, s.EventId, s.Seed.Alice, s.Clock.GetUtcNow()));
        Assert.Equal(0, await s.CountAsync("""
            select count(*) from events.registrations where event_id=$1 and user_id=$2 and checked_in_at is not null
            """, s.EventId, s.Seed.Bob));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.outbox_events where type='events.waitlisted_registration_promoted'
              and resource_id=$1 and payload->>'userId'=$2
            """, s.EventId, s.Seed.Bob.ToString("D")));
        Assert.Equal(1, await s.CountAsync("""
            select count(*) from ops.audit_events where action='events.registration_withdrawn_by_suspension'
              and target_id=$1
            """, s.EventId));

        Assert.True(Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice, commandId)).Replayed);
        Assert.IsType<SuspensionOutcome.AlreadySuspended>(await s.SuspendAsync(s.Seed.Alice));
        await s.AssertCapacityAsync(1);
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.outbox_events where type='events.waitlisted_registration_promoted'"));
    }

    [Theory]
    [InlineData(-10, true)]
    [InlineData(0, false)]
    [InlineData(10, false)]
    public async Task PromotionStopsAtTheStartEvenWhenTheLifecycleJobIsLate(long ticksFromStart, bool promotes)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        s.Clock.SetUtcNow(s.StartsAt.AddTicks(ticksFromStart));

        Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice));

        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
        Assert.Equal(promotes ? "Confirmed" : "Waitlisted", await s.StateAsync(s.Seed.Bob));
        await s.AssertCapacityAsync(promotes ? 1 : 0);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("email")]
    [InlineData("selfie")]
    public async Task PromotionSkipsIneligiblePeopleWithoutChangingTheirQueuePosition(string missing)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.RegisterAsync(s.Seed.Third);
        await s.ExecuteAsync(missing switch
        {
            "status" => "update identity.users set status='Restricted' where id=$1",
            "email" => "update identity.users set email_confirmed=false where id=$1",
            _ => "delete from identity.selfie_verifications where user_id=$1",
        }, s.Seed.Bob);

        Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice));

        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Bob));
        Assert.Equal(1, await s.CountAsync("select waitlist_position from events.registrations where event_id=$1 and user_id=$2", s.EventId, s.Seed.Bob));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Third));
        await s.AssertCapacityAsync(1);
    }

    [Fact]
    public async Task WithdrawingFromTheWaitlistPreservesThePositionInHistoryAndDoesNotReleaseASeat()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.RegisterAsync(s.Seed.Third);

        Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Bob));

        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Bob));
        Assert.Equal(1, await s.CountAsync("select withdrawn_waitlist_position from events.registrations where event_id=$1 and user_id=$2", s.EventId, s.Seed.Bob));
        Assert.Equal(2, await s.CountAsync("select waitlist_position from events.registrations where event_id=$1 and user_id=$2", s.EventId, s.Seed.Third));
        await s.AssertCapacityAsync(1);
    }

    [Fact]
    public async Task NoEligibleWaiterLeavesTheSeatFree()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.ExecuteAsync("update identity.users set status='Restricted' where id=$1", s.Seed.Bob);

        Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice));

        await s.AssertCapacityAsync(0);
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Bob));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task ClosedEventsAreHistoricalAndRemainUnchanged(string state)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.ExecuteAsync("update events.events set state=$2 where id=$1", s.EventId, state);

        Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice));

        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Alice));
        await s.AssertCapacityAsync(1);
    }

    [Fact]
    public async Task LiftingDoesNotRestoreTheRegistrationOrPermitAnotherRegistrationForTheSameEvent()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await using var factory = new DuoraWebFactory(s.Database.AppConnectionString) { Time = s.Clock };
        var scenario = new AccountSuspensionScenario(s.Database.AppConnectionString, factory, s.Clock);
        var (_, moderator) = await scenario.PrivilegedBrowserAsync();
        using var browser = moderator;
        var suspended = await AccountSuspensionScenario.SuspendAsync(browser, s.Seed.Alice);
        var suspensionId = Guid.Parse(suspended["suspensionId"]!.GetValue<string>());
        using var lift = await browser.PostJsonAsync(AccountSuspensionScenario.LiftPath(s.Seed.Alice, suspensionId),
            new { internalReason = "Synthetic review." }, AccountSuspensionScenario.WithIfMatch(suspended["eTag"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.OK, lift.StatusCode);

        Assert.Equal(ParticipationOutcome.Unavailable, await s.RegisterAsync(s.Seed.Alice));
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
        await s.AssertCapacityAsync(0);
        Assert.Equal(1, await s.CountAsync("select count(*) from events.registrations where event_id=$1 and user_id=$2", s.EventId, s.Seed.Alice));
        Assert.Equal(ParticipationOutcome.Done, await s.Participation.RegisterAsync(s.Seed.Events[1], s.Seed.Alice, false, s.Token));
    }
}
