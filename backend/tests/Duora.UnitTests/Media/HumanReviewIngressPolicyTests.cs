using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class HumanReviewIngressPolicyTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("testing")]
    public void HumanReviewIngressCanBeQualifiedWithSyntheticData(string environment)
    {
        Assert.True(MediaReview.IsPermitted(HumanReview(), environment));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("Staging")]
    [InlineData("Produção")]
    [InlineData("")]
    public void HumanReviewIngressDoesNotAdmitProductionOrUnknownEnvironments(string environment)
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
