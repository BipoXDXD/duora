using Duora.Core.Events;
using static Duora.UnitTests.Events.EventFixtures;
using static Duora.UnitTests.Events.EventStates;

namespace Duora.UnitTests.Events;

public sealed class CheckInTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    [Fact]
    public void ConfirmedPersonChecksInInsideTheWindow()
    {
        var @event = CheckInOpenWith(capacity: 2, registrations: 1);

        var outcome = @event.CheckIn(User(1), ParticipantEligibility.Eligible, DuringCheckIn);

        AssertAccepted(outcome);
        Assert.Empty(outcome.Effects);
        Assert.Equal(RegistrationState.CheckedIn, @event.RegistrationOf(User(1))?.State);
        Assert.Equal(DuringCheckIn, @event.RegistrationOf(User(1))?.CheckedInAt);
        Assert.Equal(1, @event.ConfirmedCount);
    }

    [Fact]
    public void CheckInIsAcceptedAtTheOpeningInstant()
    {
        var @event = CheckInOpenWith(capacity: 2, registrations: 1);

        AssertAccepted(@event.CheckIn(User(1), ParticipantEligibility.Eligible, CheckInOpensAt));
    }

    [Fact]
    public void CheckInIsRefusedJustBeforeTheOpeningInstant()
    {
        var @event = PublishedWith(capacity: 2, registrations: 1);

        var outcome = @event.CheckIn(User(1), ParticipantEligibility.Eligible, CheckInOpensAt - Tick);

        AssertRefused(CheckInRefusal.CheckInNotOpenYet, outcome);
        Assert.Equal(RegistrationState.Confirmed, @event.RegistrationOf(User(1))?.State);
    }

    [Fact]
    public void CheckInIsAcceptedJustBeforeTheStartInstant()
    {
        var @event = CheckInOpenWith(capacity: 2, registrations: 1);

        AssertAccepted(@event.CheckIn(User(1), ParticipantEligibility.Eligible, StartsAt - Tick));
    }

    [Fact]
    public void CheckInIsRefusedFromTheStartInstant()
    {
        var @event = CheckInOpenWith(capacity: 2, registrations: 1);

        var outcome = @event.CheckIn(User(1), ParticipantEligibility.Eligible, StartsAt);

        AssertRefused(CheckInRefusal.CheckInClosed, outcome);
        Assert.Equal(RegistrationState.Confirmed, @event.RegistrationOf(User(1))?.State);
    }

    [Fact]
    public void TheWindowDecidesEvenIfTheEventWasNotMovedToCheckInOpenYet()
    {
        var @event = PublishedWith(capacity: 2, registrations: 1);

        AssertAccepted(@event.CheckIn(User(1), ParticipantEligibility.Eligible, CheckInOpensAt));
    }

    [Fact]
    public void TheWindowFollowsTheParameterSnapshot()
    {
        var parameters = new EventParameters(
            ParameterSetId,
            version: 2,
            maxParticipants: 100,
            roundCount: 3,
            roundWindow: TimeSpan.FromMinutes(12),
            checkInLeadTime: TimeSpan.FromMinutes(30),
            roundStartTolerance: TimeSpan.FromMinutes(1));
        var creation = Event.CreateDraft(EventId, new EventSchedule(StartsAt, parameters), 2, Experience, BeforeCheckIn);
        Assert.True(creation.IsCreated);
        var @event = creation.Draft;
        @event.Publish(ExperienceVersionStatus.Published, BeforeCheckIn);
        @event.Register(RegistrationId(1), User(1), ParticipantEligibility.Eligible, BeforeCheckIn);

        AssertRefused(
            CheckInRefusal.CheckInNotOpenYet,
            @event.CheckIn(User(1), ParticipantEligibility.Eligible, StartsAt.AddMinutes(-30) - Tick));
        AssertAccepted(@event.CheckIn(User(1), ParticipantEligibility.Eligible, StartsAt.AddMinutes(-30)));
    }

    [Fact]
    public void WaitlistedPersonCannotCheckIn()
    {
        var @event = CheckInOpenWith(capacity: 1, registrations: 2);

        var outcome = @event.CheckIn(User(2), ParticipantEligibility.Eligible, DuringCheckIn);

        AssertRefused(CheckInRefusal.RegistrationNotConfirmed, outcome);
        Assert.Equal(RegistrationState.Waitlisted, @event.RegistrationOf(User(2))?.State);
    }

    [Fact]
    public void WithdrawnPersonCannotCheckIn()
    {
        var @event = CheckInOpenWith(capacity: 2, registrations: 1);
        @event.Withdraw(User(1), DuringCheckIn);

        var outcome = @event.CheckIn(User(1), ParticipantEligibility.Eligible, DuringCheckIn);

        AssertRefused(CheckInRefusal.RegistrationNotConfirmed, outcome);
    }

    [Fact]
    public void PersonWithoutRegistrationCannotCheckIn()
    {
        var @event = CheckInOpenWith(capacity: 2, registrations: 1);

        AssertRefused(
            CheckInRefusal.NotRegistered, @event.CheckIn(User(9), ParticipantEligibility.Eligible, DuringCheckIn));
    }

    [Fact]
    public void IneligiblePersonCannotCheckIn()
    {
        var @event = CheckInOpenWith(capacity: 2, registrations: 1);

        var outcome = @event.CheckIn(User(1), ParticipantEligibility.Ineligible, DuringCheckIn);

        AssertRefused(CheckInRefusal.ParticipantIneligible, outcome);
        Assert.Equal(RegistrationState.Confirmed, @event.RegistrationOf(User(1))?.State);
    }

    [Fact]
    public void RepeatedCheckInIsAcceptedWithoutChangingTheFirstOne()
    {
        var @event = CheckInOpenWith(capacity: 2, registrations: 1);
        @event.CheckIn(User(1), ParticipantEligibility.Eligible, CheckInOpensAt);

        var outcome = @event.CheckIn(User(1), ParticipantEligibility.Eligible, DuringCheckIn);

        AssertAccepted(outcome);
        Assert.Empty(outcome.Effects);
        Assert.Equal(CheckInOpensAt, @event.RegistrationOf(User(1))?.CheckedInAt);
    }

    [Fact]
    public void PromotedPersonCanCheckIn()
    {
        var @event = CheckInOpenWith(capacity: 1, registrations: 2);
        @event.Withdraw(User(1), DuringCheckIn);

        AssertAccepted(@event.CheckIn(User(2), ParticipantEligibility.Eligible, DuringCheckIn));
    }

    [Theory]
    [InlineData(EventState.Draft)]
    [InlineData(EventState.Running)]
    [InlineData(EventState.Completed)]
    [InlineData(EventState.Cancelled)]
    public void CheckInIsRefusedWhenTheEventDoesNotAcceptIt(EventState state)
    {
        var @event = In(state);

        AssertRefused(
            CheckInRefusal.EventNotAcceptingCheckIn,
            @event.CheckIn(User(1), ParticipantEligibility.Eligible, DuringCheckIn));
    }

    private static Event PublishedWith(int capacity, int registrations)
    {
        var @event = Published(capacity);
        for (var number = 1; number <= registrations; number++)
        {
            @event.Register(RegistrationId(number), User(number), ParticipantEligibility.Eligible, BeforeCheckIn);
        }

        return @event;
    }

    private static Event CheckInOpenWith(int capacity, int registrations)
    {
        var @event = PublishedWith(capacity, registrations);
        AssertAccepted(@event.OpenCheckIn(CheckInOpensAt));
        return @event;
    }
}
