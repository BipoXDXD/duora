using Duora.Core.Media;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Theory]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    public async Task ConfirmedMissingCandidateCanBeReconciledWithoutRestoringOwnerPublicationEligibility(string status)
    {
        var item = await CaseAsync();
        await ExecuteAsync("update identity.users set status=$2 where id=$1", item.OwnerId, status);
        await LoseCandidateVariantAsync(item, PhotoVariant.Full);
        var history = await AvailabilityHistoryAsync();
        var files = Files();

        Assert.Equal(1, await Availability().ReconcileAsync(item.CaseId, Ct));

        await AssertUnavailableCandidateAsync(item);
        Assert.Equal(1, await ScalarAsync("select count(*) from identity.users where id=$1 and status=$2", item.OwnerId, status));
        Assert.Equal(history, await AvailabilityHistoryAsync());
        Assert.Equal(files, Files());
    }
}
