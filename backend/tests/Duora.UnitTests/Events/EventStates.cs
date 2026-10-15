using Duora.Core.Events;
using static Duora.UnitTests.Events.EventFixtures;

namespace Duora.UnitTests.Events;

/// <summary>Leva um evento novo a cada estado pelo caminho público, nos instantes em que cada transição vale.</summary>
internal static class EventStates
{
    public static readonly CancellationReason Reason = new("Local indisponível");

    public static Event Draft(int capacity = 10)
    {
        var creation = Event.CreateDraft(EventId, Schedule, capacity, Experience, BeforeCheckIn);
        Assert.True(creation.IsCreated, $"Rascunho recusado: {creation.Refusal}");
        return creation.Draft;
    }

    public static Event Published(int capacity = 10)
    {
        var @event = Draft(capacity);
        AssertAccepted(@event.Publish(ExperienceVersionStatus.Published, BeforeCheckIn));
        return @event;
    }

    public static Event CheckInOpen(int capacity = 10)
    {
        var @event = Published(capacity);
        AssertAccepted(@event.OpenCheckIn(CheckInOpensAt));
        return @event;
    }

    public static Event Running(int capacity = 10)
    {
        var @event = CheckInOpen(capacity);
        AssertAccepted(@event.Start(StartsAt));
        return @event;
    }

    public static Event Completed(int capacity = 10)
    {
        var @event = Running(capacity);
        AssertAccepted(@event.Complete(EndsAt));
        return @event;
    }

    public static Event Cancelled(int capacity = 10)
    {
        var @event = Published(capacity);
        AssertAccepted(@event.Cancel(AdminId, Reason, BeforeCheckIn));
        return @event;
    }

    public static Event In(EventState state) => state switch
    {
        EventState.Draft => Draft(),
        EventState.Published => Published(),
        EventState.CheckInOpen => CheckInOpen(),
        EventState.Running => Running(),
        EventState.Completed => Completed(),
        EventState.Cancelled => Cancelled(),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Estado sem caminho de teste."),
    };

    public static void AssertAccepted<TRefusal>(Outcome<TRefusal> outcome)
        where TRefusal : struct, Enum =>
        Assert.True(outcome.IsAccepted, $"Recusado: {outcome.Refusal}");

    public static void AssertRefused<TRefusal>(TRefusal expected, Outcome<TRefusal> outcome)
        where TRefusal : struct, Enum
    {
        Assert.Equal(expected, outcome.Refusal);
        Assert.Empty(outcome.Effects);
    }
}
