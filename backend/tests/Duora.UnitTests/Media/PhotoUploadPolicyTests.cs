using Duora.Core.Identity;
using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class PhotoUploadPolicyTests
{
    [Theory]
    [InlineData(AccountStatus.PendingEligibility, true)]
    [InlineData(AccountStatus.Active, true)]
    [InlineData(AccountStatus.PendingEmail, false)]
    [InlineData(AccountStatus.Restricted, false)]
    [InlineData(AccountStatus.Suspended, false)]
    [InlineData(AccountStatus.DeletionPending, false)]
    public void OnlyAccountsThatMayEditTheirProfileMayUploadPhotos(AccountStatus status, bool allowed)
    {
        Assert.Equal(allowed, PhotoUploadPolicy.Allows(status));
    }
}
