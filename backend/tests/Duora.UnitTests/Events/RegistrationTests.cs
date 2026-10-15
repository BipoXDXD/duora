using Duora.Core.Events;
using static Duora.UnitTests.Events.EventFixtures;
using static Duora.UnitTests.Events.EventStates;

namespace Duora.UnitTests.Events;

public sealed class RegistrationTests
{
    private static readonly DateTimeOffset Now = BeforeCheckIn.AddHours(1);

    [Fact]
    public void RegistrationWithAFreeSeatIsConfirmed()
    {
        var @event = Published(capacity: 1);

        var outcome = @event.Register(RegistrationId(1), User(1), ParticipantEligibility.Eligible, Now);

        AssertAccepted(outcome);
        Assert.Empty(outcome.Effects);
        var registration = Assert.Single(@event.Registrations);
        Assert.Equal(RegistrationId(1), registration.Id);
        Assert.Equal(User(1), registration.UserId);
        Assert.Equal(RegistrationState.Confirmed, registration.State);
        Assert.Null(registration.WaitlistPosition);
        Assert.Equal(Now, registration.RegisteredAt);
        Assert.Equal(Now, registration.ConfirmedAt);
        Assert.Equal(1, @event.ConfirmedCount);
    }

    [Fact]
    public void RegistrationWithoutAFreeSeatJoinsTheWaitlist()
    {
        var @event = WithRegistrations(capacity: 1, count: 1);

        AssertAccepted(@event.Register(RegistrationId(2), User(2), ParticipantEligibility.Eligible, Now));

        var registration = @event.RegistrationOf(User(2));
        Assert.NotNull(registration);
        Assert.Equal(RegistrationState.Waitlisted, registration.State);
        Assert.Equal(1, registration.WaitlistPosition);
        Assert.Null(registration.ConfirmedAt);
        Assert.Equal(1, @event.ConfirmedCount);
    }

    [Fact]
    public void LastSeatGoesToTheFirstAndTheNextOneWaits()
    {
        var @event = WithRegistrations(capacity: 3, count: 2);

        @event.Register(RegistrationId(3), User(3), ParticipantEligibility.Eligible, Now);
        @event.Register(RegistrationId(4), User(4), ParticipantEligibility.Eligible, Now);

        Assert.Equal(RegistrationState.Confirmed, @event.RegistrationOf(User(3))?.State);
        Assert.Equal(RegistrationState.Waitlisted, @event.RegistrationOf(User(4))?.State);
        Assert.Equal(3, @event.ConfirmedCount);
    }

    [Fact]
    public void WaitlistPositionsFollowTheRegistrationOrder()
    {
        var @event = WithRegistrations(capacity: 2, count: 5);

        Assert.Equal([User(3), User(4), User(5)], @event.Waitlist.Select(r => r.UserId));
        Assert.Equal([1L, 2L, 3L], @event.Waitlist.Select(r => r.WaitlistPosition ?? 0));
    }

    [Fact]
    public void WaitlistIsEmptyWhileThereAreFreeSeats()
    {
        var @event = WithRegistrations(capacity: 5, count: 5);

        Assert.Empty(@event.Waitlist);
        Assert.Equal(5, @event.ConfirmedCount);
    }

    [Fact]
    public void SamePersonCannotRegisterTwice()
    {
        var @event = WithRegistrations(capacity: 3, count: 1);

        var outcome = @event.Register(RegistrationId(9), User(1), ParticipantEligibility.Eligible, Now);

        AssertRefused(RegistrationRefusal.AlreadyRegistered, outcome);
        Assert.Single(@event.Registrations);
        Assert.Equal(1, @event.ConfirmedCount);
    }

    [Fact]
    public void WithdrawnPersonCannotRegisterAgain()
    {
        var @event = WithRegistrations(capacity: 3, count: 1);
        @event.Withdraw(User(1), Now);

        var outcome = @event.Register(RegistrationId(9), User(1), ParticipantEligibility.Eligible, Now);

        AssertRefused(RegistrationRefusal.AlreadyRegistered, outcome);
        Assert.Equal(RegistrationState.Withdrawn, @event.RegistrationOf(User(1))?.State);
    }

    [Fact]
    public void IneligiblePersonIsRefusedAndNothingIsRecorded()
    {
        var @event = Published();

        var outcome = @event.Register(RegistrationId(1), User(1), ParticipantEligibility.Ineligible, Now);

        AssertRefused(RegistrationRefusal.ParticipantIneligible, outcome);
        Assert.Empty(@event.Registrations);
    }

    [Theory]
    [InlineData(EventState.Draft)]
    [InlineData(EventState.Running)]
    [InlineData(EventState.Completed)]
    [InlineData(EventState.Cancelled)]
    public void RegistrationIsRefusedOutsidePublishedAndCheckInOpen(EventState state)
    {
        var @event = In(state);

        var outcome = @event.Register(RegistrationId(1), User(1), ParticipantEligibility.Eligible, CheckInOpensAt);

        AssertRefused(RegistrationRefusal.EventNotOpenForRegistration, outcome);
        Assert.Empty(@event.Registrations);
    }

    [Fact]
    public void RegistrationIsAcceptedDuringCheckIn()
    {
        var @event = CheckInOpen();

        AssertAccepted(@event.Register(RegistrationId(1), User(1), ParticipantEligibility.Eligible, DuringCheckIn));
    }

    [Fact]
    public void RegistrationIsRefusedFromTheStartInstantEvenIfTheEventWasNotStartedYet()
    {
        var @event = CheckInOpen();

        var outcome = @event.Register(RegistrationId(1), User(1), ParticipantEligibility.Eligible, StartsAt);

        AssertRefused(RegistrationRefusal.EventNotOpenForRegistration, outcome);
    }

    [Fact]
    public void RegistrationIsAcceptedJustBeforeTheStartInstant()
    {
        var @event = CheckInOpen();

        var outcome = @event.Register(
            RegistrationId(1), User(1), ParticipantEligibility.Eligible, StartsAt - TimeSpan.FromTicks(1));

        AssertAccepted(outcome);
    }

    [Fact]
    public void RegistrationRejectsEmptyIds()
    {
        var @event = Published();

        Assert.Throws<ArgumentException>(
            () => @event.Register(Guid.Empty, User(1), ParticipantEligibility.Eligible, Now));
        Assert.Throws<ArgumentException>(
            () => @event.Register(RegistrationId(1), Guid.Empty, ParticipantEligibility.Eligible, Now));
    }

    [Fact]
    public void WithdrawingAConfirmedPersonWithAnEmptyWaitlistOnlyFreesTheSeat()
    {
        var @event = WithRegistrations(capacity: 2, count: 2);
        var withdrawnAt = Now.AddMinutes(5);

        var outcome = @event.Withdraw(User(1), withdrawnAt);

        AssertAccepted(outcome);
        Assert.Empty(outcome.Effects);
        Assert.Equal(RegistrationState.Withdrawn, @event.RegistrationOf(User(1))?.State);
        Assert.Equal(withdrawnAt, @event.RegistrationOf(User(1))?.WithdrawnAt);
        Assert.Equal(1, @event.ConfirmedCount);
    }

    [Fact]
    public void WithdrawingAConfirmedPersonPromotesTheOnlyWaitlistedOne()
    {
        var @event = WithRegistrations(capacity: 1, count: 2);
        var withdrawnAt = Now.AddMinutes(5);

        var outcome = @event.Withdraw(User(1), withdrawnAt);

        Assert.Equal([new WaitlistedRegistrationPromoted(EventId, User(2), withdrawnAt)], outcome.Effects);
        var promoted = @event.RegistrationOf(User(2));
        Assert.Equal(RegistrationState.Confirmed, promoted?.State);
        Assert.Null(promoted?.WaitlistPosition);
        Assert.Equal(withdrawnAt, promoted?.ConfirmedAt);
        Assert.Empty(@event.Waitlist);
        Assert.Equal(1, @event.ConfirmedCount);
    }

    [Fact]
    public void SuccessiveWithdrawalsPromoteInWaitlistOrder()
    {
        var @event = WithRegistrations(capacity: 2, count: 5);

        var first = @event.Withdraw(User(2), Now);
        var second = @event.Withdraw(User(1), Now);

        Assert.Equal([new WaitlistedRegistrationPromoted(EventId, User(3), Now)], first.Effects);
        Assert.Equal([new WaitlistedRegistrationPromoted(EventId, User(4), Now)], second.Effects);
        Assert.Equal([User(5)], @event.Waitlist.Select(r => r.UserId));
        Assert.Equal(2, @event.ConfirmedCount);
    }

    [Fact]
    public void WithdrawingFromTheWaitlistKeepsSeatsAndTheOrderOfTheOthers()
    {
        var @event = WithRegistrations(capacity: 1, count: 4);

        var outcome = @event.Withdraw(User(3), Now);

        AssertAccepted(outcome);
        Assert.Empty(outcome.Effects);
        Assert.Equal(RegistrationState.Withdrawn, @event.RegistrationOf(User(3))?.State);
        Assert.Null(@event.RegistrationOf(User(3))?.WaitlistPosition);
        Assert.Equal([User(2), User(4)], @event.Waitlist.Select(r => r.UserId));
        Assert.Equal(1, @event.ConfirmedCount);
    }

    [Fact]
    public void PromotionSkipsWhoLeftTheWaitlist()
    {
        var @event = WithRegistrations(capacity: 1, count: 3);
        @event.Withdraw(User(2), Now);

        var outcome = @event.Withdraw(User(1), Now);

        Assert.Equal([new WaitlistedRegistrationPromoted(EventId, User(3), Now)], outcome.Effects);
    }

    [Fact]
    public void WithdrawingACheckedInPersonDuringCheckInPromotesTheWaitlist()
    {
        var @event = WithRegistrations(capacity: 1, count: 2);
        @event.OpenCheckIn(CheckInOpensAt);
        @event.CheckIn(User(1), ParticipantEligibility.Eligible, DuringCheckIn);

        var outcome = @event.Withdraw(User(1), DuringCheckIn);

        Assert.Equal([new WaitlistedRegistrationPromoted(EventId, User(2), DuringCheckIn)], outcome.Effects);
    }

    [Fact]
    public void WithdrawingWhileRunningFreesTheSeatWithoutPromotion()
    {
        var @event = WithRegistrations(capacity: 1, count: 2);
        @event.OpenCheckIn(CheckInOpensAt);
        @event.CheckIn(User(1), ParticipantEligibility.Eligible, DuringCheckIn);
        @event.Start(StartsAt);

        var outcome = @event.Withdraw(User(1), StartsAt.AddMinutes(1));

        AssertAccepted(outcome);
        Assert.Empty(outcome.Effects);
        Assert.Equal(RegistrationState.Withdrawn, @event.RegistrationOf(User(1))?.State);
        Assert.Equal(RegistrationState.Waitlisted, @event.RegistrationOf(User(2))?.State);
        Assert.Equal(0, @event.ConfirmedCount);
    }

    [Fact]
    public void WithdrawingTwiceIsRefusedTheSecondTime()
    {
        var @event = WithRegistrations(capacity: 1, count: 2);
        @event.Withdraw(User(1), Now);

        var outcome = @event.Withdraw(User(1), Now);

        AssertRefused(WithdrawalRefusal.RegistrationNotActive, outcome);
        Assert.Equal(RegistrationState.Confirmed, @event.RegistrationOf(User(2))?.State);
    }

    [Fact]
    public void WithdrawingWithoutRegistrationIsRefused()
    {
        var @event = WithRegistrations(capacity: 1, count: 1);

        AssertRefused(WithdrawalRefusal.NotRegistered, @event.Withdraw(User(9), Now));
    }

    [Theory]
    [InlineData(EventState.Completed)]
    [InlineData(EventState.Cancelled)]
    public void WithdrawingAfterTheEventFinishedIsRefused(EventState state)
    {
        var @event = In(state);

        AssertRefused(WithdrawalRefusal.EventClosed, @event.Withdraw(User(1), EndsAt));
    }

    [Fact]
    public void StartMarksConfirmedPeopleWithoutCheckInAsNoShow()
    {
        var @event = WithRegistrations(capacity: 2, count: 3);
        @event.OpenCheckIn(CheckInOpensAt);
        @event.CheckIn(User(1), ParticipantEligibility.Eligible, DuringCheckIn);

        var outcome = @event.Start(StartsAt);

        Assert.Empty(outcome.Effects);
        Assert.Equal(RegistrationState.CheckedIn, @event.RegistrationOf(User(1))?.State);
        Assert.Equal(RegistrationState.NoShow, @event.RegistrationOf(User(2))?.State);
        Assert.Equal(RegistrationState.Waitlisted, @event.RegistrationOf(User(3))?.State);
        Assert.Equal(1, @event.ConfirmedCount);
    }

    [Fact]
    public void CompleteMarksCheckedInPeopleAsCompleted()
    {
        var @event = WithRegistrations(capacity: 2, count: 2);
        @event.OpenCheckIn(CheckInOpensAt);
        @event.CheckIn(User(1), ParticipantEligibility.Eligible, DuringCheckIn);
        @event.Start(StartsAt);

        @event.Complete(EndsAt);

        Assert.Equal(RegistrationState.Completed, @event.RegistrationOf(User(1))?.State);
        Assert.Equal(RegistrationState.NoShow, @event.RegistrationOf(User(2))?.State);
    }

    [Fact]
    public void CancelNotifiesEveryActiveRegistrationInRegistrationOrder()
    {
        var @event = WithRegistrations(capacity: 1, count: 3);
        @event.Withdraw(User(2), Now);

        var outcome = @event.Cancel(AdminId, Reason, Now);

        Assert.Equal(
            [
                new EventCancelled(EventId, AdminId, Reason, Now),
                new CancellationNoticeRequested(EventId, User(1), Now),
                new CancellationNoticeRequested(EventId, User(3), Now),
            ],
            outcome.Effects);
    }

    private static Event WithRegistrations(int capacity, int count)
    {
        var @event = Published(capacity);
        for (var number = 1; number <= count; number++)
        {
            AssertAccepted(@event.Register(RegistrationId(number), User(number), ParticipantEligibility.Eligible, Now));
        }

        return @event;
    }
}
