using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

/// <summary>
/// Qual solicitação decide a elegibilidade quando a pessoa tem mais de uma (ADR-0024, adendo de 2026-10-16): a mais
/// recente, salvo um pedido sem decisão posterior a uma verificação adulta. Na dúvida, não elegível.
/// </summary>
public sealed class AgeEligibilityDecidingVerificationTests
{
    [Theory]
    [InlineData(AgeVerificationState.Pending)]
    [InlineData(AgeVerificationState.Expired)]
    public void AdultVerificationPrevailsOverALaterRequestWithoutDecision(AgeVerificationState latest)
    {
        Assert.True(AgeEligibility.AdultVerificationPrevails(AgeVerificationState.VerifiedAdult, latest));
    }

    [Theory]
    [InlineData(AgeVerificationState.Rejected)]
    [InlineData(AgeVerificationState.ManualReview)]
    [InlineData(AgeVerificationState.VerifiedAdult)]
    public void LatestDecisionIsTheOneThatCounts(AgeVerificationState latest)
    {
        Assert.False(AgeEligibility.AdultVerificationPrevails(AgeVerificationState.VerifiedAdult, latest));
    }

    [Theory]
    [InlineData(AgeVerificationState.Rejected, AgeVerificationState.Pending)]
    [InlineData(AgeVerificationState.Rejected, AgeVerificationState.Expired)]
    [InlineData(AgeVerificationState.ManualReview, AgeVerificationState.Expired)]
    public void OnlyAnAdultVerificationOutweighsALaterRequest(
        AgeVerificationState latestDecision,
        AgeVerificationState latest)
    {
        Assert.False(AgeEligibility.AdultVerificationPrevails(latestDecision, latest));
    }

    [Fact]
    public void DecisionsAreTheStatesSomeoneRuledOn()
    {
        Assert.Equal(
            [AgeVerificationState.VerifiedAdult, AgeVerificationState.Rejected, AgeVerificationState.ManualReview],
            AgeEligibility.Decisions);
    }
}
