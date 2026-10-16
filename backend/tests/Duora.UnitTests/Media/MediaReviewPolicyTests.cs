using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class MediaReviewPolicyTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void TechnicalOnlyReviewIsAllowedOutsideProduction(string environment)
    {
        Assert.True(MediaReview.IsPermitted(MediaReviewPolicy.TechnicalOnly, environment));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    public void TechnicalOnlyReviewIsRefusedInProduction(string environment)
    {
        // Sem revisão editorial (spec 12.3), foto real não pode ser aprovada só pela checagem técnica.
        Assert.False(MediaReview.IsPermitted(MediaReviewPolicy.TechnicalOnly, environment));
    }

    [Fact]
    public void TechnicalOnlyApprovesWhatPassedTheSanitizer()
    {
        Assert.Equal(UploadState.Approved, MediaReview.VerdictAfterSanitizing(MediaReviewPolicy.TechnicalOnly));
    }
}
