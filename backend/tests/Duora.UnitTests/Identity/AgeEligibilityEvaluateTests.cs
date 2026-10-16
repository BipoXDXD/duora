using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

/// <summary>
/// Elegibilidade etária e próximo passo (spec 12.2, API-P06, TM-40): só conta ativa com verificação adulta dentro da
/// validade participa; nada vira elegível por decurso de prazo.
/// </summary>
public sealed class AgeEligibilityEvaluateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly AgeVerificationPolicy Policy = new(
        "2026-10-15",
        validity: TimeSpan.FromDays(365),
        requestLifetime: TimeSpan.FromHours(24));

    [Fact]
    public void ActiveAccountWithAVerificationInsideTheValidityIsEligible()
    {
        var latest = Verified(expiresAt: Now.AddDays(1));

        var eligibility = AgeEligibility.Evaluate(AccountStatus.Active, latest, Now, Policy);

        Assert.True(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.None, eligibility.NextStep);
    }

    [Fact]
    public void VerificationThatExpiresNowNoLongerCounts()
    {
        var latest = Verified(expiresAt: Now);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.Active, latest, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.StartVerification, eligibility.NextStep);
    }

    [Fact]
    public void VerificationOneTickBeforeItsExpiryStillCounts()
    {
        var latest = Verified(expiresAt: Now.AddTicks(1));

        Assert.True(AgeEligibility.Evaluate(AccountStatus.Active, latest, Now, Policy).IsEligible);
    }

    [Fact]
    public void PendingEligibilityWithoutAnyRequestMustStartOne()
    {
        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest: null, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.StartVerification, eligibility.NextStep);
    }

    [Fact]
    public void ActiveAccountWithoutAnyVerificationIsNotEligible()
    {
        var eligibility = AgeEligibility.Evaluate(AccountStatus.Active, latest: null, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.StartVerification, eligibility.NextStep);
    }

    [Fact]
    public void OpenRequestWithoutTheProviderFlowWaitsForTheProvider()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Pending, Now.AddMinutes(-1), ExpiresAt: null, ProviderFlowReady: false);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.WaitForProvider, eligibility.NextStep);
    }

    [Fact]
    public void OpenRequestWithTheProviderFlowContinuesThere()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Pending, Now.AddMinutes(-1), ExpiresAt: null, ProviderFlowReady: true);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy);

        Assert.Equal(AgeVerificationNextStep.ContinueWithProvider, eligibility.NextStep);
    }

    [Fact]
    public void RequestAbandonedForTheWholeLifetimeCanBeStartedAgain()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Pending, Now.AddHours(-24), ExpiresAt: null, ProviderFlowReady: true);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.StartVerification, eligibility.NextStep);
    }

    [Fact]
    public void RequestOneTickYoungerThanTheLifetimeIsStillOpen()
    {
        var latest = new AgeVerificationSnapshot(
            AgeVerificationState.Pending,
            Now.AddHours(-24).AddTicks(1),
            ExpiresAt: null,
            ProviderFlowReady: false);

        Assert.Equal(
            AgeVerificationNextStep.WaitForProvider,
            AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy).NextStep);
    }

    [Fact]
    public void ManualReviewWaitsForTheReviewAndNeverExpiresIntoANewRequest()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.ManualReview, Now.AddDays(-30), ExpiresAt: null, ProviderFlowReady: true);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.WaitForReview, eligibility.NextStep);
    }

    [Fact]
    public void ExpiredRequestCanBeStartedAgain()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Expired, Now.AddDays(-400), Now.AddDays(-35), ProviderFlowReady: true);

        Assert.Equal(
            AgeVerificationNextStep.StartVerification,
            AgeEligibility.Evaluate(AccountStatus.Active, latest, Now, Policy).NextStep);
    }

    [Theory]
    [InlineData(AccountStatus.Restricted)]
    [InlineData(AccountStatus.Suspended)]
    [InlineData(AccountStatus.DeletionPending)]
    [InlineData(AccountStatus.PendingEmail)]
    public void AccountOutsideTheVerificationFlowIsNeverEligibleEvenWithAValidVerification(AccountStatus account)
    {
        var eligibility = AgeEligibility.Evaluate(account, Verified(expiresAt: Now.AddDays(1)), Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.NotAvailable, eligibility.NextStep);
    }

    [Fact]
    public void RejectedRequestOffersNoNewRequest()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Rejected, Now.AddDays(-1), ExpiresAt: null, ProviderFlowReady: true);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.NotAvailable, eligibility.NextStep);
    }

    [Fact]
    public void PendingEligibilityWithAValidVerificationIsStillNotEligible()
    {
        // Não acontece pelo fluxo (a mesma transação grava as duas coisas); se acontecer, nega.
        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, Verified(Now.AddDays(1)), Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.NotAvailable, eligibility.NextStep);
    }

    [Theory]
    [InlineData(AgeVerificationNextStep.StartVerification, true)]
    [InlineData(AgeVerificationNextStep.None, false)]
    [InlineData(AgeVerificationNextStep.WaitForProvider, false)]
    [InlineData(AgeVerificationNextStep.ContinueWithProvider, false)]
    [InlineData(AgeVerificationNextStep.WaitForReview, false)]
    [InlineData(AgeVerificationNextStep.NotAvailable, false)]
    public void OnlyStartVerificationAllowsANewRequest(AgeVerificationNextStep step, bool allowed)
    {
        Assert.Equal(allowed, new AgeEligibilityResult(IsEligible: false, step).AllowsNewRequest);
    }

    private static AgeVerificationSnapshot Verified(DateTimeOffset expiresAt) =>
        new(AgeVerificationState.VerifiedAdult, expiresAt.AddDays(-365), expiresAt, ProviderFlowReady: true);
}
