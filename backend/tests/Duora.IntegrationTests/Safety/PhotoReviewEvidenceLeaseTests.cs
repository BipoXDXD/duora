using System.Security.Cryptography;
using Duora.Infrastructure.Safety;

namespace Duora.IntegrationTests.Safety;

public sealed class PhotoReviewEvidenceLeaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryBoundedChunkRequiresFreshAuthorizationAndDisposalClearsPrivateBytes()
    {
        var bytes = RandomNumberGenerator.GetBytes(128 * 1024);
        var expected = bytes.ToArray();
        var checks = 0;
        await using var lease = new PhotoReviewEvidenceLease(bytes, _ =>
        {
            Interlocked.Increment(ref checks);
            return Task.FromResult(true);
        });
        var destination = new byte[128 * 1024];

        Assert.Equal(64 * 1024, await lease.ReadAsync(destination, Ct));
        Assert.Equal(expected.Take(64 * 1024), destination.Take(64 * 1024));
        Assert.Equal(1, checks);
        Assert.Equal(64 * 1024, await lease.ReadAsync(destination.AsMemory(64 * 1024), Ct));
        Assert.Equal(expected, destination);
        Assert.Equal(2, checks);
        await lease.DisposeAsync();
        Assert.All(bytes, value => Assert.Equal((byte)0, value));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.ReadAsync(destination, Ct));
    }

    [Fact]
    public async Task AFailedChunkAuthorizationNeverWritesTheDestinationAndDisposesSensitiveBytes()
    {
        var bytes = RandomNumberGenerator.GetBytes(128);
        var destination = Enumerable.Repeat((byte)0xa5, 32).ToArray();
        await using var lease = new PhotoReviewEvidenceLease(bytes, _ => Task.FromResult(false));

        await Assert.ThrowsAsync<PhotoReviewEvidenceRevokedException>(() => lease.ReadAsync(destination, Ct));

        Assert.All(destination, value => Assert.Equal((byte)0xa5, value));
        Assert.All(bytes, value => Assert.Equal((byte)0, value));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task AnAuthorizationInfrastructureFailureAlsoClearsPrivateBytesBeforeReturning()
    {
        var bytes = RandomNumberGenerator.GetBytes(128);
        var destination = Enumerable.Repeat((byte)0xa5, 32).ToArray();
        await using var lease = new PhotoReviewEvidenceLease(bytes, _ => throw new IOException("Synthetic authorization commit failure"));

        await Assert.ThrowsAsync<IOException>(() => lease.ReadAsync(destination, Ct));

        Assert.All(destination, value => Assert.Equal((byte)0xa5, value));
        Assert.All(bytes, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task DisposeCancelsAndObservesAnActiveAuthorizationBeforeClearingItsBuffer()
    {
        var bytes = RandomNumberGenerator.GetBytes(128);
        var destination = Enumerable.Repeat((byte)0xa5, 32).ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        var checks = 0;
        using var fallback = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var lease = new PhotoReviewEvidenceLease(bytes, async cancellationToken =>
        {
            Interlocked.Increment(ref checks);
            entered.TrySetResult();
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, fallback.Token);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token);
                return true;
            }
            finally
            {
                finished = true;
            }
        });
        var reading = lease.ReadAsync(destination, Ct);
        var queuedDestination = Enumerable.Repeat((byte)0xa5, 8).ToArray();
        Task<int>? queued = null;
        Task? firstDispose = null;
        Task? secondDispose = null;
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, reading).WaitAsync(TimeSpan.FromSeconds(5), Ct));
            queued = lease.ReadAsync(queuedDestination, Ct);
            Assert.Equal(1, checks);
            firstDispose = lease.DisposeAsync().AsTask();
            secondDispose = lease.DisposeAsync().AsTask();
            await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(reading.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            Assert.True(queued.IsCompleted);
            var queuedFailure = await Record.ExceptionAsync(() => queued);
            Assert.True(queuedFailure is OperationCanceledException or ObjectDisposedException);
            Assert.All(queuedDestination, value => Assert.Equal((byte)0xa5, value));
            Assert.Equal(1, checks);
            Assert.True(finished);
            Assert.All(bytes, value => Assert.Equal((byte)0, value));
            Assert.All(destination, value => Assert.Equal((byte)0xa5, value));
        }
        finally
        {
            await fallback.CancelAsync();
            firstDispose ??= lease.DisposeAsync().AsTask();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(firstDispose, secondDispose ?? firstDispose).WaitAsync(cleanup.Token);
            try
            {
                await Task.WhenAll(reading, queued ?? reading).WaitAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (reading.IsCompleted && (queued?.IsCompleted ?? true))
            {
            }
            catch (PhotoReviewEvidenceRevokedException) when (reading.IsCompleted && (queued?.IsCompleted ?? true))
            {
            }
            catch (ObjectDisposedException) when (reading.IsCompleted && (queued?.IsCompleted ?? true))
            {
            }
        }
    }
}
