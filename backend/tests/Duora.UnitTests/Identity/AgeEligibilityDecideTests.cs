using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

/// <summary>
/// Efeito de um resultado do fornecedor sobre a solicitação e sobre a conta (ADR-0024, decisões 4 e 5). Os valores
/// esperados são literais: a tabela é a regra.
/// </summary>
public sealed class AgeEligibilityDecideTests
{
    private static readonly DateTimeOffset DecidedAt = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly AgeVerificationPolicy Policy = new(
        "2026-10-15",
        validity: TimeSpan.FromDays(365),
        requestLifetime: TimeSpan.FromHours(24));

    [Fact]
    public void VerifiedAdultActivatesAPendingEligibilityAccountAndSetsTheExpiry()
    {
        var decision = AgeEligibility.Decide(
            AccountStatus.PendingEligibility,
            AgeVerificationState.Pending,
            AgeCheckOutcome.VerifiedAdult,
            DecidedAt,
            Policy);

        Assert.NotNull(decision);
        Assert.Equal(AgeVerificationState.VerifiedAdult, decision.VerificationState);
        Assert.Equal(new DateTimeOffset(2027, 10, 15, 12, 0, 0, TimeSpan.Zero), decision.ExpiresAt);
        Assert.Equal(AccountStatus.Active, decision.AccountStatus);
        Assert.True(decision.ChangesAccountFrom(AccountStatus.PendingEligibility));
    }

    [Fact]
    public void NotAdultRejectsTheRequestAndRestrictsThePendingAccount()
    {
        var decision = AgeEligibility.Decide(
            AccountStatus.PendingEligibility,
            AgeVerificationState.Pending,
            AgeCheckOutcome.NotAdult,
            DecidedAt,
            Policy);

        Assert.NotNull(decision);
        Assert.Equal(AgeVerificationState.Rejected, decision.VerificationState);
        Assert.Null(decision.ExpiresAt);
        Assert.Equal(AccountStatus.Restricted, decision.AccountStatus);
    }

    [Fact]
    public void NotAdultRestrictsAnActiveAccountThatIsVerifyingAgain()
    {
        var decision = AgeEligibility.Decide(
            AccountStatus.Active,
            AgeVerificationState.Pending,
            AgeCheckOutcome.NotAdult,
            DecidedAt,
            Policy);

        Assert.Equal(AccountStatus.Restricted, decision?.AccountStatus);
    }

    [Fact]
    public void InconclusiveSendsTheRequestToManualReviewAndKeepsTheAccountPending()
    {
        var decision = AgeEligibility.Decide(
            AccountStatus.PendingEligibility,
            AgeVerificationState.Pending,
            AgeCheckOutcome.Inconclusive,
            DecidedAt,
            Policy);

        Assert.NotNull(decision);
        Assert.Equal(AgeVerificationState.ManualReview, decision.VerificationState);
        Assert.Null(decision.ExpiresAt);
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
            AgeCheckOutcome.VerifiedAdult,
            DecidedAt,
            Policy);

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
            AgeCheckOutcome.NotAdult,
            DecidedAt,
            Policy);

        Assert.Equal(account, decision?.AccountStatus);
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
            AgeCheckOutcome.VerifiedAdult,
            DecidedAt,
            Policy);

        Assert.Null(decision);
    }

    [Fact]
    public void RejectsAnOutcomeOutsideTheEnum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgeEligibility.Decide(
            AccountStatus.PendingEligibility,
            AgeVerificationState.Pending,
            (AgeCheckOutcome)99,
            DecidedAt,
            Policy));
    }
}
