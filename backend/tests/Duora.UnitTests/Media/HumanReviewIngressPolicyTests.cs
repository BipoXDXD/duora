using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class HumanReviewIngressPolicyTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("testing")]
    [InlineData("Production")]
    [InlineData("production")]
    public void HumanReviewIsAdmittedInSyntheticEnvironmentsAndInProduction(string environment)
    {
        Assert.True(MediaReview.IsPermitted(HumanReview(), environment));
    }

    // Owner decision of 2026-10-28 admits Production only; a new environment still needs its own decision.
    [Theory]
    [InlineData("Staging")]
    [InlineData("Produção")]
    [InlineData("Production ")]
    [InlineData("")]
    public void HumanReviewIsNotAdmittedInUnknownEnvironments(string environment)
    {
        Assert.False(MediaReview.IsPermitted(HumanReview(), environment));
    }

    [Fact]
    public void SuccessfulSanitizationRequiresAHumanDecisionBeforeApproval()
    {
        Assert.Equal(UploadState.AwaitingReview, MediaReview.VerdictAfterSanitizing(HumanReview()));
        Assert.True(UploadLifecycle.CanMove(UploadState.Processing, UploadState.AwaitingReview));
        Assert.False(UploadLifecycle.CanMove(UploadState.AwaitingReview, UploadState.Approved));
        Assert.True(UploadLifecycle.CountsAsPending(UploadState.AwaitingReview));
    }

    private static MediaReviewPolicy HumanReview()
    {
        Assert.True(Enum.TryParse<MediaReviewPolicy>("HumanReview", out var policy));
        return policy;
    }
}
