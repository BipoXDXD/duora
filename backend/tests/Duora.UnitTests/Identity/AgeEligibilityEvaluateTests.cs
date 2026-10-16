using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

/// <summary>
/// Elegibilidade etária e próximo passo (spec 12.2, API-P06, TM-40; ADR-0024, adendo de 2026-10-16): só conta ativa
/// com verificação adulta participa, e a verificação adulta não vence; nada vira elegível por decurso de prazo.
/// </summary>
public sealed class AgeEligibilityEvaluateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly AgeVerificationPolicy Policy = new("2026-10-15", requestLifetime: TimeSpan.FromHours(24));

    [Fact]
    public void ActiveAccountWithAnAdultVerificationIsEligible()
    {
        var eligibility = AgeEligibility.Evaluate(AccountStatus.Active, Verified(requestedAt: Now.AddDays(-1)), Now, Policy);

        Assert.True(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.None, eligibility.NextStep);
    }

    [Theory]
    [InlineData(AgeVerificationState.VerifiedAdult)]
    [InlineData(AgeVerificationState.Expired)]
    [InlineData(AgeVerificationState.Pending)]
    [InlineData(AgeVerificationState.ManualReview)]
    public void MinorSuspicionMakesAnActiveAccountIneligibleWhateverTheDecidingRequest(AgeVerificationState state)
    {
        var latest = new AgeVerificationSnapshot(state, Now.AddDays(-1), ProviderFlowReady: true, MinorSuspected: true);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.Active, latest, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.NotAvailable, eligibility.NextStep);
    }

    [Theory]
    [InlineData(366)]
    [InlineData(3650)]
    [InlineData(36500)]
    public void AdultVerificationNeverExpires(int daysAgo)
    {
        var eligibility = AgeEligibility.Evaluate(AccountStatus.Active, Verified(requestedAt: Now.AddDays(-daysAgo)), Now, Policy);

        Assert.True(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.None, eligibility.NextStep);
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
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Pending, Now.AddMinutes(-1), ProviderFlowReady: false);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.WaitForProvider, eligibility.NextStep);
    }

    [Fact]
    public void OpenRequestWithTheProviderFlowContinuesThere()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Pending, Now.AddMinutes(-1), ProviderFlowReady: true);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy);

        Assert.Equal(AgeVerificationNextStep.ContinueWithProvider, eligibility.NextStep);
    }

    [Fact]
    public void RequestAbandonedForTheWholeLifetimeCanBeStartedAgain()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Pending, Now.AddHours(-24), ProviderFlowReady: true);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.StartVerification, eligibility.NextStep);
    }

    [Fact]
    public void RequestOneTickYoungerThanTheLifetimeIsStillOpen()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Pending, Now.AddHours(-24).AddTicks(1), ProviderFlowReady: false);

        Assert.Equal(
            AgeVerificationNextStep.WaitForProvider,
            AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy).NextStep);
    }

    [Fact]
    public void ManualReviewWaitsForTheReviewAndNeverExpiresIntoANewRequest()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.ManualReview, Now.AddDays(-30), ProviderFlowReady: true);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.WaitForReview, eligibility.NextStep);
    }

    [Fact]
    public void ExpiredRequestCanBeStartedAgain()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Expired, Now.AddDays(-2), ProviderFlowReady: true);

        Assert.Equal(
            AgeVerificationNextStep.StartVerification,
            AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy).NextStep);
    }

    [Theory]
    [InlineData(AccountStatus.Restricted)]
    [InlineData(AccountStatus.Suspended)]
    [InlineData(AccountStatus.DeletionPending)]
    [InlineData(AccountStatus.PendingEmail)]
    public void AccountOutsideTheVerificationFlowIsNeverEligibleEvenWithAnAdultVerification(AccountStatus account)
    {
        var eligibility = AgeEligibility.Evaluate(account, Verified(requestedAt: Now.AddDays(-1)), Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.NotAvailable, eligibility.NextStep);
    }

    [Fact]
    public void RejectedRequestOffersNoNewRequest()
    {
        var latest = new AgeVerificationSnapshot(AgeVerificationState.Rejected, Now.AddDays(-1), ProviderFlowReady: true);

        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, latest, Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.NotAvailable, eligibility.NextStep);
    }

    [Fact]
    public void PendingEligibilityWithAnAdultVerificationIsNotEligibleAndIsNeverAskedToVerifyAgain()
    {
        // Não acontece pelo fluxo: o mesmo callback grava VerifiedAdult e ativa a conta. Quem um dia devolver a conta
        // ao fluxo a leva a Active (ADR-0024, adendo de 2026-10-16); até lá, nega sem pedir nova verificação.
        var eligibility = AgeEligibility.Evaluate(AccountStatus.PendingEligibility, Verified(requestedAt: Now.AddDays(-1)), Now, Policy);

        Assert.False(eligibility.IsEligible);
        Assert.Equal(AgeVerificationNextStep.NotAvailable, eligibility.NextStep);
        Assert.False(eligibility.AllowsNewRequest);
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

    private static AgeVerificationSnapshot Verified(DateTimeOffset requestedAt) =>
        new(AgeVerificationState.VerifiedAdult, requestedAt, ProviderFlowReady: true);
}
