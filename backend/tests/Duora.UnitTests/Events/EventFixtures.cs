using Duora.Core.Events;

namespace Duora.UnitTests.Events;

/// <summary>Dados sintéticos fixos: parâmetros iniciais da spec 4.3 e um evento às 22h UTC de 20/11/2026.</summary>
internal static class EventFixtures
{
    public static readonly Guid ParameterSetId = new("0192f3a0-0000-7000-8000-000000000001");
    public static readonly Guid ExperienceVersionId = new("0192f3a0-0000-7000-8000-000000000002");
    public static readonly Guid EventId = new("0192f3a0-0000-7000-8000-000000000003");
    public static readonly Guid AdminId = new("0192f3a0-0000-7000-8000-000000000004");

    public static readonly DateTimeOffset StartsAt = new(2026, 11, 20, 22, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset CheckInOpensAt = StartsAt.AddMinutes(-10);
    public static readonly DateTimeOffset EndsAt = StartsAt.AddMinutes(36);

    /// <summary>Instante de criação e publicação, bem antes da abertura do check-in.</summary>
    public static readonly DateTimeOffset BeforeCheckIn = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Instante dentro da janela de check-in.</summary>
    public static readonly DateTimeOffset DuringCheckIn = StartsAt.AddMinutes(-5);

    public static readonly EventParameters Parameters = new(
        ParameterSetId,
        version: 1,
        maxParticipants: 100,
        roundCount: 3,
        roundWindow: TimeSpan.FromMinutes(12),
        checkInLeadTime: TimeSpan.FromMinutes(10),
        roundStartTolerance: TimeSpan.FromMinutes(1));

    public static readonly ExperienceVersionRef Experience = new(ExperienceVersionId, version: 2);

    public static readonly EventSchedule Schedule = new(StartsAt, Parameters);

    public static Guid User(int number) => new($"0192f3a0-0000-7000-9000-{number:D12}");

    public static Guid RegistrationId(int number) => new($"0192f3a0-0000-7000-a000-{number:D12}");
}
