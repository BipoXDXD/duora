using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

public sealed class ParticipationEligibilityTests
{
    [Theory]
    [InlineData(AccountStatus.Active, true, SelfieState.Passed, true)]
    [InlineData(AccountStatus.Active, false, SelfieState.Passed, false)]
    [InlineData(AccountStatus.PendingEligibility, true, SelfieState.Passed, false)]
    [InlineData(AccountStatus.Restricted, true, SelfieState.Passed, false)]
    [InlineData(AccountStatus.Suspended, true, SelfieState.Passed, false)]
    [InlineData(AccountStatus.DeletionPending, true, SelfieState.Passed, false)]
    [InlineData(AccountStatus.PendingEmail, false, SelfieState.Passed, false)]
    [InlineData(AccountStatus.Active, true, SelfieState.RecaptureRequired, false)]
    [InlineData(AccountStatus.Active, true, SelfieState.Inconclusive, false)]
    [InlineData(AccountStatus.Active, true, SelfieState.Expired, false)]
    [InlineData(AccountStatus.Active, true, SelfieState.Pending, false)]
    [InlineData(AccountStatus.Active, true, SelfieState.Capturing, false)]
    [InlineData(AccountStatus.Active, true, SelfieState.Processing, false)]
    [InlineData(AccountStatus.Active, true, null, false)]
    public void OnlyConfirmedActiveAccountsWithPresenceCanParticipate(
        AccountStatus account, bool emailConfirmed, SelfieState? selfie, bool expected)
        => Assert.Equal(expected, ParticipationEligibility.CanParticipate(account, emailConfirmed, selfie));

    [Theory]
    [InlineData(AccountStatus.PendingEligibility, true, SelfieState.Passed, AccountStatus.Active)]
    [InlineData(AccountStatus.PendingEligibility, false, SelfieState.Passed, AccountStatus.PendingEligibility)]
    [InlineData(AccountStatus.PendingEligibility, true, SelfieState.Processing, AccountStatus.PendingEligibility)]
    [InlineData(AccountStatus.PendingEligibility, true, null, AccountStatus.PendingEligibility)]
    [InlineData(AccountStatus.Active, true, SelfieState.Passed, AccountStatus.Active)]
    [InlineData(AccountStatus.Restricted, true, SelfieState.Passed, AccountStatus.Restricted)]
    [InlineData(AccountStatus.PendingEmail, false, SelfieState.Passed, AccountStatus.PendingEmail)]
    public void OnlyAPendingConfirmedAccountWithPresenceMovesToActive(
        AccountStatus account, bool emailConfirmed, SelfieState? selfie, AccountStatus expected)
        => Assert.Equal(expected, ParticipationEligibility.AfterPresenceCheck(account, emailConfirmed, selfie));
}
