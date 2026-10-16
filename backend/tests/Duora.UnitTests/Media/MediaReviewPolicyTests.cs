using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class MediaReviewPolicyTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("testing")]
    public void TechnicalOnlyReviewIsAllowedOnlyWithSyntheticData(string environment)
    {
        Assert.True(MediaReview.IsPermitted(MediaReviewPolicy.TechnicalOnly, environment));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("Staging")]
    [InlineData("Produção")]
    [InlineData("")]
    public void TechnicalOnlyReviewIsRefusedEverywhereElse(string environment)
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
