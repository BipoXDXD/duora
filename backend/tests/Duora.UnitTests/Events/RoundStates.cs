using Duora.Core.Events;
using static Duora.UnitTests.Events.EventFixtures;

namespace Duora.UnitTests.Events;

/// <summary>Primeira rodada do evento de teste: começa às 22h, tolera início até 22h01 e termina às 22h12.</summary>
internal static class RoundStates
{
    public static readonly Guid RoundId = new("0192f3a0-0000-7000-b000-000000000001");
    public static readonly DateTimeOffset RoundStartsAt = StartsAt;
    public static readonly DateTimeOffset StartDeadline = StartsAt.AddMinutes(1);
    public static readonly DateTimeOffset RoundEndsAt = StartsAt.AddMinutes(12);

    /// <summary>Instante em que todas as transições de início ainda valem.</summary>
    public static readonly DateTimeOffset OnTime = StartsAt.AddSeconds(30);

    public static Round Scheduled() => Round.Schedule(RoundId, EventId, number: 1, Schedule);

    public static Round Preparing()
    {
        var round = Scheduled();
        EventStates.AssertAccepted(round.BeginPreparation(round.Version, OnTime));
        return round;
    }

    public static Round Inviting()
    {
        var round = Preparing();
        EventStates.AssertAccepted(round.BeginInviting(round.Version, OnTime));
        return round;
    }

    public static Round Running()
    {
        var round = Inviting();
        EventStates.AssertAccepted(round.BeginRunning(round.Version, OnTime));
        return round;
    }

    public static Round Closed()
    {
        var round = Running();
        EventStates.AssertAccepted(round.Close(round.Version, RoundEndsAt));
        return round;
    }

    public static Round Cancelled()
    {
        var round = Scheduled();
        EventStates.AssertAccepted(round.Cancel(round.Version, OnTime));
        return round;
    }

    public static Round In(RoundState state) => state switch
    {
        RoundState.Scheduled => Scheduled(),
        RoundState.Preparing => Preparing(),
        RoundState.Inviting => Inviting(),
        RoundState.Running => Running(),
        RoundState.Closed => Closed(),
        RoundState.Cancelled => Cancelled(),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Estado sem caminho de teste."),
    };

    public static Effect[] ReleaseAndNotice(DateTimeOffset at) =>
    [
        new ReservationsReleaseRequested(EventId, RoundId, at),
        new UnavailabilityNoticeRequested(EventId, RoundId, at),
    ];
}
