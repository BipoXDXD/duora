using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class UploadLifecycleTests
{
    public static TheoryData<UploadState, UploadState> AllowedMoves => new()
    {
        { UploadState.Pending, UploadState.Quarantined },
        { UploadState.Pending, UploadState.Deleted },
        { UploadState.Quarantined, UploadState.Processing },
        { UploadState.Quarantined, UploadState.Deleted },
        { UploadState.Processing, UploadState.Processing },
        { UploadState.Processing, UploadState.Approved },
        { UploadState.Processing, UploadState.Rejected },
        { UploadState.Processing, UploadState.Deleted },
        { UploadState.Processing, UploadState.AwaitingReview },
        { UploadState.Approved, UploadState.Deleted },
        { UploadState.Rejected, UploadState.Deleted },
        { UploadState.AwaitingReview, UploadState.Deleted },
    };

    [Theory]
    [MemberData(nameof(AllowedMoves))]
    public void TheUploadAdvancesOnlyAlongThePipeline(UploadState from, UploadState to)
    {
        Assert.True(UploadLifecycle.CanMove(from, to));
    }

    [Fact]
    public void EveryOtherMoveIsRefused()
    {
        var allowed = AllowedMoves.Select(row => row.Data).ToHashSet();
        var states = Enum.GetValues<UploadState>();

        var refused = states.SelectMany(from => states.Select(to => (from, to))).Where(move => !allowed.Contains(move)).ToList();

        Assert.Contains((UploadState.Pending, UploadState.Approved), refused);
        Assert.Contains((UploadState.Quarantined, UploadState.Approved), refused);
        Assert.Contains((UploadState.Rejected, UploadState.Approved), refused);
        Assert.Contains((UploadState.Approved, UploadState.Processing), refused);
        Assert.Contains((UploadState.Deleted, UploadState.Approved), refused);
        Assert.All(refused, move => Assert.False(UploadLifecycle.CanMove(move.from, move.to), $"{move.from} → {move.to}"));
    }

    [Theory]
    [InlineData(UploadState.Pending, true)]
    [InlineData(UploadState.Quarantined, true)]
    [InlineData(UploadState.Processing, true)]
    [InlineData(UploadState.AwaitingReview, true)]
    [InlineData(UploadState.Approved, false)]
    [InlineData(UploadState.Rejected, false)]
    [InlineData(UploadState.Deleted, false)]
    public void PendingCountsEveryStateBeforeTheVerdict(UploadState state, bool pending)
    {
        Assert.Equal(pending, UploadLifecycle.CountsAsPending(state));
    }

    [Fact]
    public void ThePendingStatesAreListedForTheDatabaseQuery()
    {
        Assert.Equal(
            [UploadState.Pending, UploadState.Quarantined, UploadState.Processing, UploadState.AwaitingReview],
            UploadLifecycle.PendingStates);
    }

    [Fact]
    public void HumanReviewIngressDoesNotPermitImplicitHumanApprovalOrRejection()
    {
        var awaiting = UploadState.AwaitingReview;
        Assert.True(UploadLifecycle.CountsAsPending(awaiting));
        Assert.True(UploadLifecycle.CanMove(UploadState.Processing, awaiting));
        Assert.All(Enum.GetValues<UploadState>().Where(from => from != UploadState.Processing),
            from => Assert.False(UploadLifecycle.CanMove(from, awaiting)));
        Assert.False(UploadLifecycle.CanMove(awaiting, UploadState.Approved));
        Assert.False(UploadLifecycle.CanMove(awaiting, UploadState.Rejected));
        Assert.True(Enum.TryParse<MediaReviewPolicy>("HumanReview", out var policy));
        Assert.Equal(awaiting, MediaReview.VerdictAfterSanitizing(policy));
        Assert.False(MediaReview.IsPermitted(policy, "Production"));
    }
}
