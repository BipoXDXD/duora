using Duora.Core.Events;
using Duora.Core.Safety;

namespace Duora.UnitTests.Safety;

/// <summary>ADR-0049, protocolo, passo 9: o desfecho de uma unidade relida sob locks, sem banco.</summary>
public sealed class ReconciliationUnitRevalidationTests
{
    private const long MeasureVersion = 1;

    private static readonly Guid Measure = Guid.NewGuid();
    private static readonly Guid Approved = Guid.NewGuid();
    private static readonly DateTimeOffset DecidedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly ReconciliationPreviewItem[] Manifest =
        [new ReconciliationPreviewItem(Approved, 3, RegistrationState.Confirmed, MaxReleasedSeats: 1, MaxPromotions: 1)];

    private static readonly LegacyMeasureFacts ActiveMeasure = new(Measure, MeasureVersion, DecidedAt);

    private static ReconciliationCurrentItem Current(
        Guid? eventId = null,
        long version = 3,
        RegistrationState state = RegistrationState.Confirmed,
        int released = 1,
        int promotions = 1,
        params LegacyRegistrationIssue[] issues) =>
        new(new ReconciliationPreviewItem(eventId ?? Approved, version, state, released, promotions),
            new LegacyRegistrationClassification(new HashSet<LegacyRegistrationIssue>(issues)));

    private static ReconciliationUnitRefusal? Decide(LegacyMeasureFacts? measure, params ReconciliationCurrentItem[] items) =>
        ReconciliationUnitRevalidation.Decide(Measure, MeasureVersion, Manifest, new ReconciliationCurrentUnit(measure, items));

    [Fact]
    public void AnUnchangedUnitProceeds() =>
        Assert.Null(Decide(ActiveMeasure, Current()));

    [Fact]
    public void ALowerCeilingUnderLockStillProceeds() =>
        Assert.Null(Decide(ActiveMeasure, Current(promotions: 0)));

    [Fact]
    public void ARegistrationOutsideTheManifestDoesNotStopTheUnit() =>
        Assert.Null(Decide(ActiveMeasure, Current(), Current(eventId: Guid.NewGuid(), issues: LegacyRegistrationIssue.RegisteredAfterOriginal)));

    [Fact]
    public void WithoutAnActiveMeasureItIsAChange() =>
        Assert.Equal(new ReconciliationUnitRefusal(ReconciliationUnitState.SkippedChanged, ReconciliationUnitRevalidation.MeasureChanged),
            Decide(null, Current()));

    [Fact]
    public void AnotherMeasureIsAChange() =>
        Assert.Equal(new ReconciliationUnitRefusal(ReconciliationUnitState.SkippedChanged, ReconciliationUnitRevalidation.MeasureChanged),
            Decide(ActiveMeasure with { Id = Guid.NewGuid() }, Current()));

    [Fact]
    public void AnotherVersionOfTheMeasureIsAChange() =>
        Assert.Equal(new ReconciliationUnitRefusal(ReconciliationUnitState.SkippedChanged, ReconciliationUnitRevalidation.MeasureChanged),
            Decide(ActiveMeasure with { Version = MeasureVersion + 1 }, Current()));

    [Fact]
    public void AMissingRegistrationIsAChange() =>
        Assert.Equal(new ReconciliationUnitRefusal(ReconciliationUnitState.SkippedChanged, ReconciliationUnitRevalidation.RegistrationChanged),
            Decide(ActiveMeasure, Current(eventId: Guid.NewGuid())));

    [Theory]
    [InlineData(2, RegistrationState.Confirmed)]
    [InlineData(4, RegistrationState.Confirmed)]
    [InlineData(3, RegistrationState.CheckedIn)]
    public void AnotherVersionOrStateIsAChange(long version, RegistrationState state) =>
        Assert.Equal(new ReconciliationUnitRefusal(ReconciliationUnitState.SkippedChanged, ReconciliationUnitRevalidation.RegistrationChanged),
            Decide(ActiveMeasure, Current(version: version, state: state)));

    [Fact]
    public void AnApprovedRegistrationThatStoppedBeingACandidateIsSkippedWithItsIssue() =>
        Assert.Equal(new ReconciliationUnitRefusal(ReconciliationUnitState.SkippedChanged, nameof(LegacyRegistrationIssue.ClosedEvent)),
            Decide(ActiveMeasure, Current(issues: LegacyRegistrationIssue.ClosedEvent)));

    [Fact]
    public void AnIssueThatBlocksTheUnitStopsItEvenOutsideTheManifest() =>
        Assert.Equal(new ReconciliationUnitRefusal(ReconciliationUnitState.SkippedChanged, nameof(LegacyRegistrationIssue.CapacityMismatch)),
            Decide(ActiveMeasure, Current(), Current(eventId: Guid.NewGuid(), issues: LegacyRegistrationIssue.CapacityMismatch)));

    [Fact]
    public void AnUnexpectedReservationNeedsReview() =>
        Assert.Equal(new ReconciliationUnitRefusal(ReconciliationUnitState.NeedsReview, nameof(LegacyRegistrationIssue.ActiveReservation)),
            Decide(ActiveMeasure, Current(), Current(eventId: Guid.NewGuid(), issues: LegacyRegistrationIssue.ActiveReservation)));

    [Fact]
    public void AChangeWinsOverAReservation() =>
        Assert.Equal(new ReconciliationUnitRefusal(ReconciliationUnitState.SkippedChanged, ReconciliationUnitRevalidation.RegistrationChanged),
            Decide(ActiveMeasure, Current(version: 4, issues: LegacyRegistrationIssue.ActiveReservation)));

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void ACeilingExceededUnderLockNeedsReview(int released, int promotions) =>
        Assert.Equal(new ReconciliationUnitRefusal(ReconciliationUnitState.NeedsReview, ReconciliationUnitRevalidation.CeilingExceeded),
            Decide(ActiveMeasure, Current(released: released, promotions: promotions)));
}
