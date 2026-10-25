using System.Net;
using System.Text;

namespace Duora.CapacityClient.Tests;

public sealed class TransportLifetimeTests
{
    [Fact]
    public async Task HeadersDoNotReleaseTheSlotWhileTheBodyIsStillPending()
    {
        using var handler = new GateHandler();
        using var client = new HttpClient(handler);
        using var budget = Budget(() => false);
        using var firstRequest = Request();
        using var secondRequest = Request();
        var first = BoundedExchange.SendAsync(client, firstRequest, HttpStatusCode.OK, budget, false, TestContext.Current.CancellationToken);
        await handler.Headers.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = BoundedExchange.SendAsync(client, secondRequest, HttpStatusCode.OK, budget, false, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Requests);
        Assert.False(second.IsCompleted);
        handler.Body.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task LeaseStopCancelsAnAlreadyPendingCleanupBody()
    {
        var stopped = 0;
        using var handler = new GateHandler();
        using var client = new HttpClient(handler);
        using var budget = Budget(() => Volatile.Read(ref stopped) != 0);
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var request = Request();
        var watch = LeaseWatch.RunAsync(budget.CheckCleanup, cleanup);
        var pending = BoundedExchange.SendAsync(client, request, HttpStatusCode.OK, budget, true, cleanup.Token);
        await handler.Headers.Task.WaitAsync(TestContext.Current.CancellationToken);
        Interlocked.Exchange(ref stopped, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Equal(LoadStopReason.Lease, await watch.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.False(handler.Body.Task.IsCompleted);
        Assert.Equal(1, handler.Requests);
    }

    private static HttpRequestMessage Request() => new(HttpMethod.Get, "https://unit-test.invalid/");
    private static PhaseBudget Budget(Func<bool> stopped)
    {
        var now = DateTimeOffset.UtcNow;
        var phase = new PhaseSpec("synthetic-unit", "only", now, now.AddSeconds(60), now.AddSeconds(120), 3, 1);
        return new PhaseBudget(phase, "source", "dataset", "producer", () => new GuardLease(
            phase.RunId, phase.PhaseId, now, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(2),
            "source", "dataset", "producer", stopped()), TimeProvider.System, TestContext.Current.CancellationToken);
    }

    // In-memory HTTP adapter: no listener, socket, DNS or project services.
    private sealed class GateHandler : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public TaskCompletionSource Headers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Body { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            HttpContent content = Interlocked.Increment(ref _requests) == 1 ? new GateContent(Body.Task) : new StringContent("{}", Encoding.UTF8);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            Headers.TrySetResult();
            return Task.FromResult(response);
        }
    }

    private sealed class GateContent(Task gate) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            _ = context;
            await gate.WaitAsync(cancellationToken);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{}"), cancellationToken);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
