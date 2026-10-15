using Duora.Core.Events;
using static Duora.UnitTests.Events.EventFixtures;
using static Duora.UnitTests.Events.EventStates;

namespace Duora.UnitTests.Events;

public sealed class EventLifecycleTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    public enum Operation
    {
        Publish,
        OpenCheckIn,
        Start,
        Complete,
        Cancel,
    }

    [Fact]
    public void DraftKeepsItsConfigurationAndStartsEmpty()
    {
        var @event = Draft(capacity: 10);

        Assert.Equal(EventId, @event.Id);
        Assert.Equal(EventState.Draft, @event.State);
        Assert.Equal(10, @event.Capacity);
        Assert.Equal(Schedule, @event.Schedule);
        Assert.Equal(Experience, @event.Experience);
        Assert.Equal(0, @event.ConfirmedCount);
        Assert.Empty(@event.Registrations);
        Assert.Null(@event.Cancellation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void DraftAcceptsCapacityFromOneToTheParameterMaximum(int capacity)
    {
        var creation = Event.CreateDraft(EventId, Schedule, capacity, Experience, BeforeCheckIn);

        Assert.True(creation.IsCreated);
        Assert.Equal(capacity, creation.Draft.Capacity);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(101)]
    public void DraftRefusesCapacityOutsideOneToTheParameterMaximum(int capacity)
    {
        var creation = Event.CreateDraft(EventId, Schedule, capacity, Experience, BeforeCheckIn);

        Assert.False(creation.IsCreated);
        Assert.Equal(EventDraftRefusal.CapacityOutOfRange, creation.Refusal);
        Assert.Null(creation.Draft);
    }

    [Fact]
    public void DraftRefusesScheduleWhoseCheckInAlreadyOpened()
    {
        var creation = Event.CreateDraft(EventId, Schedule, 10, Experience, CheckInOpensAt);

        Assert.Equal(EventDraftRefusal.ScheduleNotInFuture, creation.Refusal);
    }

    [Fact]
    public void DraftAcceptsScheduleWhoseCheckInOpensJustAfterNow()
    {
        var creation = Event.CreateDraft(EventId, Schedule, 10, Experience, CheckInOpensAt - Tick);

        Assert.True(creation.IsCreated);
    }

    [Fact]
    public void DraftRejectsEmptyId()
    {
        Assert.Throws<ArgumentException>(
            () => Event.CreateDraft(Guid.Empty, Schedule, 10, Experience, BeforeCheckIn));
    }

    [Fact]
    public void PublishMovesDraftToPublished()
    {
        var @event = Draft();

        var outcome = @event.Publish(ExperienceVersionStatus.Published, BeforeCheckIn);

        AssertAccepted(outcome);
        Assert.Empty(outcome.Effects);
        Assert.Equal(EventState.Published, @event.State);
    }

    [Theory]
    [InlineData(ExperienceVersionStatus.Draft)]
    [InlineData(ExperienceVersionStatus.Retired)]
    public void PublishRefusesExperienceVersionThatIsNotPublished(ExperienceVersionStatus status)
    {
        var @event = Draft();

        var outcome = @event.Publish(status, BeforeCheckIn);

        AssertRefused(EventTransitionRefusal.ExperienceVersionNotPublished, outcome);
        Assert.Equal(EventState.Draft, @event.State);
    }

    [Fact]
    public void PublishRefusesWhenCheckInAlreadyOpened()
    {
        var @event = Draft();

        var outcome = @event.Publish(ExperienceVersionStatus.Published, CheckInOpensAt);

        AssertRefused(EventTransitionRefusal.ScheduleNotInFuture, outcome);
        Assert.Equal(EventState.Draft, @event.State);
    }

    [Fact]
    public void PublishAcceptsJustBeforeCheckInOpens()
    {
        var @event = Draft();

        AssertAccepted(@event.Publish(ExperienceVersionStatus.Published, CheckInOpensAt - Tick));
    }

    [Fact]
    public void OpenCheckInMovesPublishedToCheckInOpenAtTheOpeningInstant()
    {
        var @event = Published();

        AssertAccepted(@event.OpenCheckIn(CheckInOpensAt));
        Assert.Equal(EventState.CheckInOpen, @event.State);
    }

    [Fact]
    public void OpenCheckInRefusesJustBeforeTheOpeningInstant()
    {
        var @event = Published();

        AssertRefused(EventTransitionRefusal.NotDueYet, @event.OpenCheckIn(CheckInOpensAt - Tick));
        Assert.Equal(EventState.Published, @event.State);
    }

    [Fact]
    public void StartMovesCheckInOpenToRunningAtTheStartInstant()
    {
        var @event = CheckInOpen();

        AssertAccepted(@event.Start(StartsAt));
        Assert.Equal(EventState.Running, @event.State);
    }

    [Fact]
    public void StartRefusesJustBeforeTheStartInstant()
    {
        var @event = CheckInOpen();

        AssertRefused(EventTransitionRefusal.NotDueYet, @event.Start(StartsAt - Tick));
        Assert.Equal(EventState.CheckInOpen, @event.State);
    }

    [Fact]
    public void CompleteMovesRunningToCompletedAtTheEndInstant()
    {
        var @event = Running();

        AssertAccepted(@event.Complete(EndsAt));
        Assert.Equal(EventState.Completed, @event.State);
    }

    [Fact]
    public void CompleteRefusesJustBeforeTheEndInstant()
    {
        var @event = Running();

        AssertRefused(EventTransitionRefusal.NotDueYet, @event.Complete(EndsAt - Tick));
        Assert.Equal(EventState.Running, @event.State);
    }

    [Theory]
    [InlineData(EventState.Draft)]
    [InlineData(EventState.Published)]
    [InlineData(EventState.CheckInOpen)]
    [InlineData(EventState.Running)]
    public void CancelIsAllowedUntilTheEventFinishes(EventState state)
    {
        var @event = In(state);

        AssertAccepted(@event.Cancel(AdminId, Reason, EndsAt));
        Assert.Equal(EventState.Cancelled, @event.State);
    }

    [Fact]
    public void CancelRecordsWhoWhyAndWhenForTheAudit()
    {
        var @event = Published();
        var cancelledAt = BeforeCheckIn.AddHours(1);

        var outcome = @event.Cancel(AdminId, Reason, cancelledAt);

        Assert.Equal(new EventCancellation(AdminId, Reason, cancelledAt), @event.Cancellation);
        Assert.Equal([new EventCancelled(EventId, AdminId, Reason, cancelledAt)], outcome.Effects);
    }

    [Fact]
    public void CancelRejectsEmptyActor()
    {
        var @event = Published();

        Assert.Throws<ArgumentException>(() => @event.Cancel(Guid.Empty, Reason, BeforeCheckIn));
    }

    [Theory]
    [MemberData(nameof(ForbiddenTransitions))]
    public void ForbiddenTransitionIsRefusedAndKeepsTheState(EventState state, Operation operation)
    {
        var @event = In(state);

        var outcome = Apply(@event, operation);

        AssertRefused(EventTransitionRefusal.InvalidState, outcome);
        Assert.Equal(state, @event.State);
    }

    public static TheoryData<EventState, Operation> ForbiddenTransitions()
    {
        var allowed = new HashSet<(EventState, Operation)>
        {
            (EventState.Draft, Operation.Publish),
            (EventState.Published, Operation.OpenCheckIn),
            (EventState.CheckInOpen, Operation.Start),
            (EventState.Running, Operation.Complete),
            (EventState.Draft, Operation.Cancel),
            (EventState.Published, Operation.Cancel),
            (EventState.CheckInOpen, Operation.Cancel),
            (EventState.Running, Operation.Cancel),
        };

        var data = new TheoryData<EventState, Operation>();
        foreach (var state in Enum.GetValues<EventState>())
        {
            foreach (var operation in Enum.GetValues<Operation>().Where(op => !allowed.Contains((state, op))))
            {
                data.Add(state, operation);
            }
        }

        return data;
    }

    /// <summary>Cada operação num instante em que a guarda de horário passaria, para isolar a guarda de estado.</summary>
    private static Outcome<EventTransitionRefusal> Apply(Event @event, Operation operation) => operation switch
    {
        Operation.Publish => @event.Publish(ExperienceVersionStatus.Published, BeforeCheckIn),
        Operation.OpenCheckIn => @event.OpenCheckIn(EndsAt),
        Operation.Start => @event.Start(EndsAt),
        Operation.Complete => @event.Complete(EndsAt),
        Operation.Cancel => @event.Cancel(AdminId, Reason, EndsAt),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Operação sem caso de teste."),
    };
}
