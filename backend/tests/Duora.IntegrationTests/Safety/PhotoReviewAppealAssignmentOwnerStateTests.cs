using Duora.Infrastructure.Safety;

namespace Duora.IntegrationTests.Safety;

public sealed partial class PhotoReviewApprovalTests
{
    [Theory]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    public async Task OpenAppealCanBeAssignedForARestrictedOwnerWithoutALiveOwnerSession(string status)
    {
        var data = await AssignableAppealAsync();
        await ExecuteAsync("update identity.users set status=$2 where id=$1", data.Data.Owner.UserId, status);
        await ExecuteAsync("update identity.login_sessions set revoked_at=$2 where id=$1",
            data.Data.Owner.SessionId, _time.GetUtcNow());
        var history = await AppealDecisionSnapshotAsync();
        var files = Files();

        var result = Assert.IsType<PhotoReviewAppealAssignmentResult.Assigned>(await AssignAppealAsync(data, Key()));

        await AssertAppealAssignmentAsync(data, result.Version, data.Reviewer.UserId);
        Assert.Equal(1, await ScalarAsync("select count(*) from identity.users where id=$1 and status=$2",
            data.Data.Owner.UserId, status));
        Assert.Equal(history, await AppealDecisionSnapshotAsync());
        Assert.Equal(files, Files());
    }
}
