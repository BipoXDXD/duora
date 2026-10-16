using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

/// <summary>
/// Efeito de um resultado do fornecedor sobre a solicitação e sobre a conta (ADR-0024, decisões 4 e 5). Os valores
/// esperados são literais: a tabela é a regra.
/// </summary>
public sealed class AgeEligibilityDecideTests
{
    [Fact]
    public void VerifiedAdultActivatesAPendingEligibilityAccountForGood()
    {
        var decision = AgeEligibility.Decide(
            AccountStatus.PendingEligibility,
            AgeVerificationState.Pending,
            AgeCheckOutcome.VerifiedAdult);

        Assert.NotNull(decision);
        Assert.Equal(AgeVerificationState.VerifiedAdult, decision.VerificationState);
        Assert.Equal(AccountStatus.Active, decision.AccountStatus);
        Assert.True(decision.ChangesAccountFrom(AccountStatus.PendingEligibility));
    }

    [Fact]
    public void NotAdultRejectsTheRequestAndRestrictsThePendingAccount()
    {
        var decision = AgeEligibility.Decide(
            AccountStatus.PendingEligibility,
            AgeVerificationState.Pending,
            AgeCheckOutcome.NotAdult);

        Assert.NotNull(decision);
        Assert.Equal(AgeVerificationState.Rejected, decision.VerificationState);
        Assert.Equal(AccountStatus.Restricted, decision.AccountStatus);
    }

    [Fact]
    public void NotAdultRestrictsAnActiveAccountThatIsVerifyingAgain()
    {
        var decision = AgeEligibility.Decide(
            AccountStatus.Active,
            AgeVerificationState.Pending,
            AgeCheckOutcome.NotAdult);

        Assert.Equal(AccountStatus.Restricted, decision?.AccountStatus);
    }

    [Fact]
    public void InconclusiveSendsTheRequestToManualReviewAndKeepsTheAccountPending()
    {
        var decision = AgeEligibility.Decide(
            AccountStatus.PendingEligibility,
            AgeVerificationState.Pending,
            AgeCheckOutcome.Inconclusive);

        Assert.NotNull(decision);
        Assert.Equal(AgeVerificationState.ManualReview, decision.VerificationState);
        Assert.Equal(AccountStatus.PendingEligibility, decision.AccountStatus);
        Assert.False(decision.ChangesAccountFrom(AccountStatus.PendingEligibility));
    }

    [Theory]
    [InlineData(AccountStatus.Restricted)]
    [InlineData(AccountStatus.Suspended)]
    [InlineData(AccountStatus.DeletionPending)]
    [InlineData(AccountStatus.PendingEmail)]
    public void VerifiedAdultNeverLiftsARestrictionOrSuspension(AccountStatus account)
    {
        var decision = AgeEligibility.Decide(
            account,
            AgeVerificationState.Pending,
            AgeCheckOutcome.VerifiedAdult);

        Assert.NotNull(decision);
        Assert.Equal(AgeVerificationState.VerifiedAdult, decision.VerificationState);
        Assert.Equal(account, decision.AccountStatus);
    }

    [Theory]
    [InlineData(AccountStatus.Suspended)]
    [InlineData(AccountStatus.DeletionPending)]
    [InlineData(AccountStatus.PendingEmail)]
    public void NotAdultDoesNotTurnASuspendedOrClosingAccountIntoRestricted(AccountStatus account)
    {
        var decision = AgeEligibility.Decide(
            account,
            AgeVerificationState.Pending,
            AgeCheckOutcome.NotAdult);

        Assert.Equal(account, decision?.AccountStatus);
    }

    [Theory]
    [InlineData(AccountStatus.PendingEligibility)]
    [InlineData(AccountStatus.Active)]
    public void LateNotAdultOnAnExpiredRequestStillRestrictsTheAccount(AccountStatus account)
    {
        var decision = AgeEligibility.Decide(account, AgeVerificationState.Expired, AgeCheckOutcome.NotAdult);

        Assert.NotNull(decision);
        Assert.Equal(AgeVerificationState.Expired, decision.VerificationState);
        Assert.Equal(AccountStatus.Restricted, decision.AccountStatus);
    }

    [Theory]
    [InlineData(AgeCheckOutcome.VerifiedAdult)]
    [InlineData(AgeCheckOutcome.Inconclusive)]
    public void LateNonRestrictingResultOnAnExpiredRequestIsIgnored(AgeCheckOutcome outcome)
    {
        Assert.Null(AgeEligibility.Decide(AccountStatus.PendingEligibility, AgeVerificationState.Expired, outcome));
    }

    [Theory]
    [InlineData(AgeVerificationState.VerifiedAdult)]
    [InlineData(AgeVerificationState.Rejected)]
    [InlineData(AgeVerificationState.Expired)]
    [InlineData(AgeVerificationState.ManualReview)]
    public void OnlyAPendingRequestAcceptsAProviderResult(AgeVerificationState state)
    {
        var decision = AgeEligibility.Decide(
            AccountStatus.PendingEligibility,
            state,
            AgeCheckOutcome.VerifiedAdult);

        Assert.Null(decision);
    }

    [Fact]
    public void RejectsAnOutcomeOutsideTheEnum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgeEligibility.Decide(
            AccountStatus.PendingEligibility,
            AgeVerificationState.Pending,
            (AgeCheckOutcome)99));
    }
}
