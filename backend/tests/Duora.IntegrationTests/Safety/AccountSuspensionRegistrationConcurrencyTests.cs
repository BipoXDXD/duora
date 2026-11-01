using Duora.Infrastructure.Participation;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Participation;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Safety;

public sealed class AccountSuspensionRegistrationConcurrencyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ATargetRegistrationCommittedDuringDiscoveryIsFoundByAFullTransactionRestart()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
        var registering = s.RegisterNamedAsync(s.Seed.Alice, "target-register");
        await s.WaitForWaiterAsync("target-register", "events.events");
        var suspending = s.SuspendAsync(s.Seed.Alice, application: "target-suspend");
        await s.WaitForWaiterAsync("target-suspend", "safety.user_guards");

        await hold.CommitAsync(s.Token);

        Assert.Equal(ParticipationOutcome.Done, await registering);
        Assert.IsType<SuspensionOutcome.Suspended>(await suspending);
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
        await s.AssertCapacityAsync(0);
        Assert.Equal(ParticipationOutcome.Unavailable, await s.RegisterAsync(s.Seed.Alice));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewThirdPartyCannotTakeTheSeatAheadOfTheWaitlist(bool suspensionFirst)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
        Task<SuspensionOutcome> suspending;
        Task<ParticipationOutcome> registering;
        if (suspensionFirst)
        {
            suspending = s.SuspendAsync(s.Seed.Alice, application: "seat-suspend");
            await s.WaitForWaiterAsync("seat-suspend", "events.events");
            registering = s.RegisterNamedAsync(s.Seed.Third, "seat-register");
            await s.WaitForWaiterAsync("seat-register", "safety.user_guards");
        }
        else
        {
            registering = s.RegisterNamedAsync(s.Seed.Third, "seat-register");
            await s.WaitForWaiterAsync("seat-register", "events.events");
            suspending = s.SuspendAsync(s.Seed.Alice, application: "seat-suspend");
            await s.WaitForWaiterAsync("seat-suspend", "safety.user_guards");
        }

        await hold.CommitAsync(s.Token);

        Assert.IsType<SuspensionOutcome.Suspended>(await suspending);
        Assert.Equal(ParticipationOutcome.Done, await registering);
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Third));
        Assert.Equal(2, await s.CountAsync("select waitlist_position from events.registrations where event_id=$1 and user_id=$2", s.EventId, s.Seed.Third));
        await s.AssertCapacityAsync(1);
        Assert.Equal(1, await s.CountAsync("select count(*) from ops.outbox_events where type='events.waitlisted_registration_promoted'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspendingTheFirstWaiterAndReleasingTheSeatSerializeInEitherOrder(bool waiterFirst)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.RegisterAsync(s.Seed.Third);
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
        var first = s.SuspendAsync(waiterFirst ? s.Seed.Bob : s.Seed.Alice, application: "first-suspend");
        await s.WaitForWaiterAsync("first-suspend", "events.events");
        var second = s.SuspendAsync(waiterFirst ? s.Seed.Alice : s.Seed.Bob, application: "second-suspend");
        await s.WaitForWaiterAsync("second-suspend", "safety.user_guards");

        await hold.CommitAsync(s.Token);

        Assert.IsType<SuspensionOutcome.Suspended>(await first);
        Assert.IsType<SuspensionOutcome.Suspended>(await second);
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Third));
        await s.AssertCapacityAsync(1);
        Assert.Equal(waiterFirst ? 1 : 2,
            await s.CountAsync("select count(*) from ops.outbox_events where type='events.waitlisted_registration_promoted'"));
    }

    [Fact]
    public async Task TwoSuspensionsReleaseTwoSeatsAndPromoteEachWaiterOnce()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres, capacity: 2);
        var extra = await BrowserScenarioSeed.InitializeAsync(s.Database.AppConnectionString, s.Clock.GetUtcNow(),
            s.Token, Guid.NewGuid().ToString("N"));
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.RegisterAsync(s.Seed.Third);
        await s.RegisterAsync(extra.Alice);
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
        var first = s.SuspendAsync(s.Seed.Alice, application: "two-first");
        await s.WaitForWaiterAsync("two-first", "events.events");
        var second = s.SuspendAsync(s.Seed.Bob, application: "two-second");
        await s.WaitForWaiterAsync("two-second", "safety.user_guards");

        await hold.CommitAsync(s.Token);

        Assert.IsType<SuspensionOutcome.Suspended>(await first);
        Assert.IsType<SuspensionOutcome.Suspended>(await second);
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Third));
        Assert.Equal("Confirmed", await s.StateAsync(extra.Alice));
        await s.AssertCapacityAsync(2);
        Assert.Equal(2, await s.CountAsync("select count(*) from ops.outbox_events where type='events.waitlisted_registration_promoted'"));
    }

    [Fact]
    public async Task EventLockTimeoutRollsBackTheMeasureHistoryCounterAuditAndOutbox()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);

        Assert.IsType<SuspensionOutcome.Busy>(await s.SuspendAsync(s.Seed.Alice));
        await hold.RollbackAsync(s.Token);

        Assert.Equal("Active", await s.TextAsync("select status from identity.users where id=$1", s.Seed.Alice));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Alice));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Bob));
        await s.AssertCapacityAsync(1);
        Assert.Equal(0, await s.CountAsync("select count(*) from safety.moderation_actions"));
        Assert.Equal(0, await s.CountAsync("select count(*) from ops.audit_events where action like 'events.%' or action='safety.account_suspended'"));
        Assert.Equal(0, await s.CountAsync("select count(*) from ops.outbox_events where type='events.waitlisted_registration_promoted'"));
    }

    [Fact]
    public async Task RegistrationPromotesAReeligibleEarlierWaiterBeforeAcceptingANewcomer()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.ExecuteAsync("update identity.users set email_confirmed=false where id=$1", s.Seed.Bob);
        Assert.IsType<SuspensionOutcome.Suspended>(await s.SuspendAsync(s.Seed.Alice));
        await s.ExecuteAsync("update identity.users set email_confirmed=true where id=$1", s.Seed.Bob);

        Assert.Equal(ParticipationOutcome.Done, await s.RegisterAsync(s.Seed.Third));

        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Third));
        await s.AssertCapacityAsync(1);
    }

    [Fact]
    public async Task AConcurrentEligibilityWriterIsObservedBeforePromotion()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.RegisterAsync(s.Seed.Third);
        await using var writer = await s.Source.OpenConnectionAsync(s.Token);
        await using var update = await writer.BeginTransactionAsync(s.Token);
        await Execute(writer, "update identity.users set email_confirmed=false where id=$1", s.Token, s.Seed.Bob);
        var suspending = s.SuspendAsync(s.Seed.Alice, application: "eligibility-suspend");
        await s.WaitForWaiterAsync("eligibility-suspend", "identity.users");

        await update.CommitAsync(s.Token);

        Assert.IsType<SuspensionOutcome.Suspended>(await suspending);
        Assert.Equal("Waitlisted", await s.StateAsync(s.Seed.Bob));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Third));
        await s.AssertCapacityAsync(1);
    }

    [Fact]
    public async Task SuspensionsAcrossTheSameEventsUseOneStableOrder()
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        var otherEvent = s.Seed.Events[1];
        await s.ExecuteAsync("update events.events set capacity=1 where id=$1", otherEvent);
        foreach (var user in new[] { s.Seed.Alice, s.Seed.Bob, s.Seed.Third })
        {
            await s.RegisterAsync(user);
        }
        foreach (var user in new[] { s.Seed.Bob, s.Seed.Alice, s.Seed.Third })
        {
            await s.Participation.RegisterAsync(otherEvent, user, false, s.Token);
        }
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=any($1) order by id for update", s.Token,
            new[] { s.EventId, otherEvent });
        var first = s.SuspendAsync(s.Seed.Alice, application: "multi-first");
        await s.WaitForWaiterAsync("multi-first", "events.events");
        var second = s.SuspendAsync(s.Seed.Bob, application: "multi-second");
        await s.WaitForWaiterAsync("multi-second", "safety.user_guards");

        await hold.CommitAsync(s.Token);

        Assert.IsType<SuspensionOutcome.Suspended>(await first);
        Assert.IsType<SuspensionOutcome.Suspended>(await second);
        await s.AssertCapacityAsync(1);
        Assert.Equal(1, await s.CountAsync("select confirmed_count from events.events where id=$1", otherEvent));
        Assert.Equal(2, await s.CountAsync("select count(*) from events.registrations where user_id=$1 and state='Confirmed'", s.Seed.Third));
        Assert.Equal(4, await s.CountAsync("select count(*) from events.registrations where state='Withdrawn'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckInAndSuspensionSerializeWithoutLosingTheHistoricalPresence(bool suspensionFirst)
    {
        await using var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await using var blocker = await s.Source.OpenConnectionAsync(s.Token);
        await using var hold = await blocker.BeginTransactionAsync(s.Token);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", s.Token, s.EventId);
        Task<SuspensionOutcome> suspending;
        Task<ParticipationOutcome> checkingIn;
        if (suspensionFirst)
        {
            suspending = s.SuspendAsync(s.Seed.Alice, application: "checkin-suspend");
            await s.WaitForWaiterAsync("checkin-suspend", "events.events");
            checkingIn = s.RegisterNamedAsync(s.Seed.Alice, "checkin-register", checkIn: true);
            await s.WaitForWaiterAsync("checkin-register", "safety.user_guards");
        }
        else
        {
            checkingIn = s.RegisterNamedAsync(s.Seed.Alice, "checkin-register", checkIn: true);
            await s.WaitForWaiterAsync("checkin-register", "events.events");
            suspending = s.SuspendAsync(s.Seed.Alice, application: "checkin-suspend");
            await s.WaitForWaiterAsync("checkin-suspend", "safety.user_guards");
        }

        await hold.CommitAsync(s.Token);

        Assert.IsType<SuspensionOutcome.Suspended>(await suspending);
        Assert.Equal(suspensionFirst ? ParticipationOutcome.Unavailable : ParticipationOutcome.Done, await checkingIn);
        Assert.Equal("Withdrawn", await s.StateAsync(s.Seed.Alice));
        Assert.Equal("Confirmed", await s.StateAsync(s.Seed.Bob));
        Assert.Equal(suspensionFirst ? 0 : 1, await s.CountAsync("""
            select count(*) from events.registrations where event_id=$1 and user_id=$2 and checked_in_at is not null
            """, s.EventId, s.Seed.Alice));
        await s.AssertCapacityAsync(1);
    }
}
