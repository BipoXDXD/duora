using Duora.Web.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

[Trait("Threat", "TM-06")]
public sealed class LoginFailureFloorTests
{
    [Theory]
    [InlineData(0.1)]
    [InlineData(349.1)]
    [InlineData(349.9)]
    public async Task FractionalMillisecondsCannotShortenTheFloor(double workMilliseconds)
    {
        var time = new FakeTimeProvider();
        var startedAt = time.GetTimestamp();
        var floor = TimeSpan.FromMilliseconds(350);
        time.Advance(TimeSpan.FromMilliseconds(workMilliseconds));

        var waiting = CredentialVerifier.WaitForFailureFloorAsync(
            startedAt, floor, time, TestContext.Current.CancellationToken);

        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(349.9 - workMilliseconds));
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(time.GetElapsedTime(startedAt) >= floor);
    }

    [Fact]
    public async Task AnAlreadyElapsedFloorDoesNotWaitAgain()
    {
        var time = new FakeTimeProvider();
        var startedAt = time.GetTimestamp();
        time.Advance(TimeSpan.FromMilliseconds(351));

        var waiting = CredentialVerifier.WaitForFailureFloorAsync(
            startedAt, TimeSpan.FromMilliseconds(350), time, TestContext.Current.CancellationToken);

        Assert.True(waiting.IsCompletedSuccessfully);
        await waiting;
    }

    [Fact]
    public async Task CancellationStillInterruptsTheFloor()
    {
        var time = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var waiting = CredentialVerifier.WaitForFailureFloorAsync(
            time.GetTimestamp(), TimeSpan.FromMilliseconds(350), time, cancellation.Token);

        Assert.False(waiting.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
