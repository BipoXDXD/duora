using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

/// <summary>
/// Estados que contam como decisão quando a pessoa tem mais de uma solicitação (ADR-0024, adendo de 2026-10-16): a
/// decisão mais recente decide; pedido sem decisão (<c>Pending</c>, <c>Expired</c>) nunca apaga uma decisão.
/// </summary>
public sealed class AgeEligibilityDecidingVerificationTests
{
    [Fact]
    public void DecisionsAreTheStatesSomeoneRuledOn()
    {
        Assert.Equal(
            [AgeVerificationState.VerifiedAdult, AgeVerificationState.Rejected, AgeVerificationState.ManualReview],
            AgeEligibility.Decisions);
    }

    [Theory]
    [InlineData(AgeVerificationState.Pending)]
    [InlineData(AgeVerificationState.Expired)]
    public void UndecidedRequestsAreNotDecisions(AgeVerificationState request)
    {
        Assert.DoesNotContain(request, AgeEligibility.Decisions);
    }
}
