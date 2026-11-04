using Duora.Core.Events;
using Duora.Core.Identity;
using Duora.Core.Safety;

namespace Duora.UnitTests.Safety;

public sealed class LegacyRegistrationSelectorTests
{
    private static readonly DateTimeOffset Lower = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Upper = Lower.AddMinutes(10);
    private static readonly ReconciliationCutover Cutover = ReconciliationCutover.Between(Lower, Upper);
    private static readonly DateTimeOffset Decided = Lower.AddDays(-1);

    private static LegacyRegistrationFacts Facts(
        LegacyAccountFacts? account = null,
        LegacyMeasuresFacts? measures = null,
        LegacyRegistrationRow? registration = null,
        LegacyEventFacts? @event = null) => new(
        account ?? new LegacyAccountFacts(AccountStatus.Suspended, HasStatusBeforeSuspension: true, IsPrivileged: false),
        measures ?? new LegacyMeasuresFacts(new LegacyMeasureFacts(Guid.NewGuid(), 1, Decided), 1, HasLifted: false),
        registration ?? new LegacyRegistrationRow(RegistrationState.Confirmed, Decided.AddHours(-1), null, null,
            WithdrawnBySuspension: false, HasActiveReservation: false),
        @event ?? new LegacyEventFacts(EventState.Published, Capacity: 2, ConfirmedCount: 1, OccupantCount: 1,
            NextWaitlistPosition: 3, HasInvalidWaitlist: false));

    private static LegacyRegistrationRow Registration(
        RegistrationState state = RegistrationState.Confirmed,
        DateTimeOffset? registeredAt = null,
        long? position = null,
        DateTimeOffset? checkedInAt = null,
        bool withdrawn = false,
        bool reservation = false) =>
        new(state, registeredAt ?? Decided.AddHours(-1), position, checkedInAt, withdrawn, reservation);

    private static LegacyRegistrationClassification Classify(LegacyRegistrationFacts facts) =>
        LegacyRegistrationSelector.Classify(facts, Cutover);

    [Theory]
    [InlineData(RegistrationState.Confirmed, null, false)]
    [InlineData(RegistrationState.Waitlisted, 2L, false)]
    [InlineData(RegistrationState.CheckedIn, null, true)]
    public void AnActiveRegistrationFromBeforeTheOriginalSuspensionIsACandidate(
        RegistrationState state, long? position, bool checkedIn)
    {
        var facts = Facts(registration: Registration(state, position: position,
            checkedInAt: checkedIn ? Decided.AddMinutes(-30) : null));

        var result = Classify(facts);

        Assert.True(result.IsCandidate);
        Assert.Null(result.Primary);
        Assert.False(result.BlocksUnit);
    }

    [Fact]
    public void AWithdrawalAlreadyAttributedToASuspensionIsNeverTouchedAgain()
    {
        var result = Classify(Facts(registration: Registration(RegistrationState.Withdrawn, withdrawn: true)));

        Assert.Equal(LegacyRegistrationIssue.AlreadyWithdrawnBySuspension, result.Primary);
        Assert.DoesNotContain(LegacyRegistrationIssue.TerminalRegistration, result.Issues);
    }

    [Theory]
    [InlineData(RegistrationState.Withdrawn)]
    [InlineData(RegistrationState.Completed)]
    public void ATerminalRegistrationWithoutProvenanceIsPreserved(RegistrationState state)
    {
        var result = Classify(Facts(registration: Registration(state)));

        Assert.Equal(LegacyRegistrationIssue.TerminalRegistration, result.Primary);
        Assert.False(result.BlocksUnit);
    }

    [Theory]
    [InlineData(EventState.Completed)]
    [InlineData(EventState.Cancelled)]
    public void ARegistrationInAClosedEventIsPreserved(EventState state)
    {
        var facts = Facts(@event: new LegacyEventFacts(state, 2, 1, 1, 3, false));

        Assert.Equal(LegacyRegistrationIssue.ClosedEvent, Classify(facts).Primary);
    }

    [Fact]
    public void ARunningEventStillAllowsTheWithdrawal()
    {
        var facts = Facts(@event: new LegacyEventFacts(EventState.Running, 2, 1, 1, 3, false));

        Assert.True(Classify(facts).IsCandidate);
    }

    [Fact]
    public void ALiftedOriginalOnAnActiveAccountIsNotReconciled()
    {
        var facts = Facts(
            account: new LegacyAccountFacts(AccountStatus.Active, false, false),
            measures: new LegacyMeasuresFacts(null, 0, HasLifted: true));

        var result = Classify(facts);

        Assert.Equal(LegacyRegistrationIssue.LiftedOriginal, result.Primary);
        Assert.Contains(LegacyRegistrationIssue.AccountNotSuspended, result.Issues);
        Assert.False(result.BlocksUnit);
    }

    [Fact]
    public void ASuspendedAccountWithoutAnyMeasureNeedsManualReview()
    {
        var result = Classify(Facts(measures: new LegacyMeasuresFacts(null, 0, HasLifted: false)));

        Assert.Equal(LegacyRegistrationIssue.MissingOriginal, result.Primary);
    }

    [Fact]
    public void TwoActiveSuspensionsAreAnIncidentThatBlocksTheUnit()
    {
        var facts = Facts(measures: new LegacyMeasuresFacts(new LegacyMeasureFacts(Guid.NewGuid(), 1, Decided), 2, false));

        var result = Classify(facts);

        Assert.Equal(LegacyRegistrationIssue.MultipleOriginals, result.Primary);
        Assert.True(result.BlocksUnit);
    }

    [Theory]
    [InlineData(AccountStatus.Active, true)]
    [InlineData(AccountStatus.Suspended, false)]
    public void AnActiveMeasureOnAnIncoherentAccountBlocksTheUnit(AccountStatus status, bool hasStatusBefore)
    {
        var result = Classify(Facts(account: new LegacyAccountFacts(status, hasStatusBefore, false)));

        Assert.Equal(LegacyRegistrationIssue.InconsistentAccount, result.Primary);
        Assert.True(result.BlocksUnit);
    }

    [Fact]
    public void APrivilegedSubjectIsExcludedAndBlocksTheUnit()
    {
        var result = Classify(Facts(account: new LegacyAccountFacts(AccountStatus.Suspended, true, IsPrivileged: true)));

        Assert.Equal(LegacyRegistrationIssue.PrivilegedSubject, result.Primary);
        Assert.True(result.BlocksUnit);
    }

    [Fact]
    public void WithoutDeploymentEvidenceNothingIsAutomatic()
    {
        var result = LegacyRegistrationSelector.Classify(Facts(), cutover: null);

        Assert.Equal(LegacyRegistrationIssue.MissingDeploymentEvidence, result.Primary);
        Assert.False(result.IsCandidate);
    }

    public static TheoryData<DateTimeOffset, LegacyRegistrationIssue?> DecisionAroundTheCutover => new()
    {
        { Lower.AddTicks(-10), null },
        { Lower, LegacyRegistrationIssue.RolloutWindow },
        { Upper.AddTicks(-10), LegacyRegistrationIssue.RolloutWindow },
        { Upper, LegacyRegistrationIssue.PostCutover },
    };

    [Theory]
    [MemberData(nameof(DecisionAroundTheCutover))]
    public void TheOriginalDecisionMustPrecedeTheLowerBoundStrictly(DateTimeOffset decided, LegacyRegistrationIssue? expected)
    {
        var facts = Facts(
            measures: new LegacyMeasuresFacts(new LegacyMeasureFacts(Guid.NewGuid(), 1, decided), 1, false),
            registration: Registration(registeredAt: decided));

        Assert.Equal(expected, Classify(facts).Primary);
    }

    [Fact]
    public void AnAtomicCutoverHasNoAmbiguousWindow()
    {
        var atomic = ReconciliationCutover.Between(Lower, Lower);
        var before = Facts(measures: new LegacyMeasuresFacts(new LegacyMeasureFacts(Guid.NewGuid(), 1, Lower.AddTicks(-10)), 1, false),
            registration: Registration(registeredAt: Lower.AddTicks(-10)));
        var at = Facts(measures: new LegacyMeasuresFacts(new LegacyMeasureFacts(Guid.NewGuid(), 1, Lower), 1, false),
            registration: Registration(registeredAt: Lower.AddTicks(-10)));

        Assert.True(LegacyRegistrationSelector.Classify(before, atomic).IsCandidate);
        Assert.Equal(LegacyRegistrationIssue.PostCutover, LegacyRegistrationSelector.Classify(at, atomic).Primary);
    }

    [Fact]
    public void ARegistrationCreatedAfterTheOriginalDecisionIsDiagnosedSeparately()
    {
        var result = Classify(Facts(registration: Registration(registeredAt: Decided.AddTicks(10))));

        Assert.Equal(LegacyRegistrationIssue.RegisteredAfterOriginal, result.Primary);
        Assert.False(result.BlocksUnit);
    }

    [Fact]
    public void AnInfiniteRegistrationTimestampIsUntrusted()
    {
        var facts = Facts(registration: new LegacyRegistrationRow(RegistrationState.Confirmed, null, null, null, false, false));

        Assert.Equal(LegacyRegistrationIssue.TimestampUntrusted, Classify(facts).Primary);
    }

    public static TheoryData<LegacyRegistrationRow> InvalidHistories => new()
    {
        Registration(RegistrationState.Waitlisted, position: null),
        Registration(RegistrationState.Waitlisted, position: 0),
        Registration(RegistrationState.Waitlisted, position: 3),
        Registration(RegistrationState.Confirmed, position: 1),
        Registration(RegistrationState.CheckedIn, checkedInAt: null),
        Registration(RegistrationState.Confirmed, checkedInAt: Decided.AddMinutes(-30)),
        Registration(RegistrationState.CheckedIn, registeredAt: Decided.AddHours(-1), checkedInAt: Decided.AddHours(-2)),
    };

    [Theory]
    [MemberData(nameof(InvalidHistories))]
    public void AnIncoherentRegistrationHistoryBlocksTheUnit(LegacyRegistrationRow registration)
    {
        var result = Classify(Facts(registration: registration));

        Assert.Equal(LegacyRegistrationIssue.InvalidHistory, result.Primary);
        Assert.True(result.BlocksUnit);
    }

    [Fact]
    public void AnInvalidWaitlistInTheEventBlocksTheUnit()
    {
        var result = Classify(Facts(@event: new LegacyEventFacts(EventState.Published, 2, 1, 1, 3, HasInvalidWaitlist: true)));

        Assert.Equal(LegacyRegistrationIssue.InvalidHistory, result.Primary);
    }

    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(1, 2, 2)]
    public void ACounterThatDisagreesWithTheOccupantsBlocksTheUnit(int capacity, int confirmed, int occupants)
    {
        var facts = Facts(@event: new LegacyEventFacts(EventState.Published, capacity, confirmed, occupants, 3, false));

        var result = Classify(facts);

        Assert.Equal(LegacyRegistrationIssue.CapacityMismatch, result.Primary);
        Assert.True(result.BlocksUnit);
    }

    [Fact]
    public void AnActiveReservationIsASeparateAnomalyThatBlocksTheUnit()
    {
        var result = Classify(Facts(registration: Registration(reservation: true)));

        Assert.Equal(LegacyRegistrationIssue.ActiveReservation, result.Primary);
        Assert.True(result.BlocksUnit);
    }

    [Fact]
    public void AllReasonsAreReportedAndThePrimaryFollowsPrecedence()
    {
        var facts = Facts(
            account: new LegacyAccountFacts(AccountStatus.Suspended, true, IsPrivileged: true),
            registration: Registration(RegistrationState.Withdrawn, registeredAt: Decided.AddTicks(10), reservation: true),
            @event: new LegacyEventFacts(EventState.Cancelled, 2, 1, 1, 3, false));

        var result = Classify(facts);

        Assert.Equal(LegacyRegistrationIssue.TerminalRegistration, result.Primary);
        Assert.Equal(
            [
                LegacyRegistrationIssue.TerminalRegistration,
                LegacyRegistrationIssue.ClosedEvent,
                LegacyRegistrationIssue.RegisteredAfterOriginal,
                LegacyRegistrationIssue.PrivilegedSubject,
            ],
            result.Issues.Order());
    }

    [Fact]
    public void ARegistrationAfterTheOriginalInsideTheRolloutWindowKeepsBothDiagnostics()
    {
        var decided = Lower.AddMinutes(1);
        var facts = Facts(
            measures: new LegacyMeasuresFacts(new LegacyMeasureFacts(Guid.NewGuid(), 1, decided), 1, false),
            registration: Registration(registeredAt: decided.AddTicks(10)));

        Assert.Equal(
            [LegacyRegistrationIssue.RolloutWindow, LegacyRegistrationIssue.RegisteredAfterOriginal],
            Classify(facts).Issues.Order());
    }

    [Fact]
    public void AnUntrustedTimestampDoesNotHideThePostCutoverDiagnostic()
    {
        var facts = Facts(
            measures: new LegacyMeasuresFacts(new LegacyMeasureFacts(Guid.NewGuid(), 1, Upper), 1, false),
            registration: new LegacyRegistrationRow(RegistrationState.Confirmed, null, null, null, false, false));

        Assert.Equal(
            [LegacyRegistrationIssue.TimestampUntrusted, LegacyRegistrationIssue.PostCutover],
            Classify(facts).Issues.Order());
    }

    [Fact]
    public void AnInfiniteCheckInTimestampIsUntrustedEvenWithoutEvidence()
    {
        var registration = Registration() with { CheckedInAtUntrusted = true };

        var result = LegacyRegistrationSelector.Classify(Facts(registration: registration), cutover: null);

        Assert.Equal(
            [LegacyRegistrationIssue.MissingDeploymentEvidence, LegacyRegistrationIssue.TimestampUntrusted],
            result.Issues.Order());
    }

    [Fact]
    public void AnInfiniteEventStartIsUntrusted()
    {
        var facts = Facts(@event: new LegacyEventFacts(EventState.Published, 2, 1, 1, 3, false, StartsAtUntrusted: true));

        Assert.Equal(LegacyRegistrationIssue.TimestampUntrusted, Classify(facts).Primary);
    }

    [Fact]
    public void AnAnomalyInAClosedEventDoesNotPauseTheUnit()
    {
        var facts = Facts(
            registration: Registration(reservation: true),
            @event: new LegacyEventFacts(EventState.Completed, 2, 0, 1, 3, HasInvalidWaitlist: true));

        var result = Classify(facts);

        Assert.Equal([LegacyRegistrationIssue.ClosedEvent], result.Issues.Order());
        Assert.False(result.BlocksUnit);
    }

    [Fact]
    public void ThePrecedenceFollowsTheProposalGroups() => Assert.Equal(
        [
            LegacyRegistrationIssue.AlreadyWithdrawnBySuspension,
            LegacyRegistrationIssue.TerminalRegistration,
            LegacyRegistrationIssue.ClosedEvent,
            LegacyRegistrationIssue.MissingOriginal,
            LegacyRegistrationIssue.MultipleOriginals,
            LegacyRegistrationIssue.LiftedOriginal,
            LegacyRegistrationIssue.AccountNotSuspended,
            LegacyRegistrationIssue.InconsistentAccount,
            LegacyRegistrationIssue.MissingDeploymentEvidence,
            LegacyRegistrationIssue.TimestampUntrusted,
            LegacyRegistrationIssue.PostCutover,
            LegacyRegistrationIssue.RolloutWindow,
            LegacyRegistrationIssue.RegisteredAfterOriginal,
            LegacyRegistrationIssue.PrivilegedSubject,
            LegacyRegistrationIssue.InvalidHistory,
            LegacyRegistrationIssue.CapacityMismatch,
            LegacyRegistrationIssue.ActiveReservation,
        ],
        Enum.GetValues<LegacyRegistrationIssue>().Order());

    public static TheoryData<DateTimeOffset, DateTimeOffset> InvalidCutovers => new()
    {
        { Upper, Lower },
        { new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.FromHours(-3)), Upper },
        { DateTimeOffset.MinValue, Upper },
        { Lower, DateTimeOffset.MaxValue },
    };

    [Theory]
    [MemberData(nameof(InvalidCutovers))]
    public void ACutoverNeedsOrderedFiniteUtcBounds(DateTimeOffset lower, DateTimeOffset upper) =>
        Assert.ThrowsAny<ArgumentException>(() => ReconciliationCutover.Between(lower, upper));
}
