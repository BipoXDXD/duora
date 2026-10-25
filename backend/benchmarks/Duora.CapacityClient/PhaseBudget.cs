using System.Net;
using System.Net.Sockets;

namespace Duora.CapacityClient;

public enum LoadStopReason { Profile, Target, Capability, Lease, Budget, Deadline, Authentication, Transport, Cleanup }

public sealed class LoadRejectedException(LoadStopReason reason) : Exception("capacity-client-refused")
{
    public LoadStopReason Reason { get; } = reason;
}

public sealed record TargetBinding(string RunId, string Address, int Port, string ServerName)
{
    public void Validate()
    {
        if (!Guid.TryParseExact(RunId, "N", out _) || Port is < 40000 or > 60999 ||
            ServerName != "qualification." + RunId + ".invalid" ||
            !IPAddress.TryParse(Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            address.ToString() != Address)
        { throw new LoadRejectedException(LoadStopReason.Target); }
        var b = address.GetAddressBytes();
        if (b[0] is 0 or 10 or 127 || b[0] >= 224 ||
            (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) ||
            (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127) ||
            (b[0] == 198 && b[1] is 18 or 19))
        { throw new LoadRejectedException(LoadStopReason.Target); }
    }
}

public sealed record PhaseSpec(string RunId, string PhaseId, DateTimeOffset StartedAt, DateTimeOffset ActiveEnd,
    DateTimeOffset CleanupEnd, int MaxRequests, int MaxInFlight);

public sealed record GuardLease(string RunId, string PhaseId, DateTimeOffset PhaseStartedAt,
    DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt, string SourceCommit, string DatasetSha256,
    string ProducerSha256, bool Stop);

public sealed class PhaseBudget : IDisposable
{
    private readonly PhaseSpec _phase;
    private readonly string _source;
    private readonly string _dataset;
    private readonly string _producer;
    private readonly Func<GuardLease> _readLease;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _stop;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly long _started;
    private readonly TimeSpan _duration;
    private long _requests;
    private int _cleanupRequests;

    public PhaseBudget(PhaseSpec phase, string sourceCommit, string datasetSha256,
        string producerSha256, Func<GuardLease> readLease, TimeProvider clock, CancellationToken stop)
    {
        _phase = phase;
        _source = sourceCommit;
        _dataset = datasetSha256;
        _producer = producerSha256;
        _readLease = readLease;
        _clock = clock;
        _stop = stop;
        _started = clock.GetTimestamp();
        _duration = phase.ActiveEnd - clock.GetUtcNow();
        if (phase.MaxRequests is < 1 or > 1000000 || phase.MaxInFlight is < 1 or > 100 ||
            _duration <= TimeSpan.Zero || phase.StartedAt > clock.GetUtcNow() ||
            phase.CleanupEnd <= phase.ActiveEnd || phase.CleanupEnd - phase.ActiveEnd > TimeSpan.FromMinutes(2))
        { throw new LoadRejectedException(LoadStopReason.Profile); }
        _slots = new SemaphoreSlim(phase.MaxInFlight, phase.MaxInFlight);
    }
    public long StartedRequests => Interlocked.Read(ref _requests);
    public TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(0, Math.Min(
        (_phase.ActiveEnd - _clock.GetUtcNow()).Ticks,
        (_duration - _clock.GetElapsedTime(_started)).Ticks)));
    public void Check()
    {
        _stop.ThrowIfCancellationRequested();
        if (Remaining <= TimeSpan.Zero)
        { throw new LoadRejectedException(LoadStopReason.Deadline); }
        CheckLease();
    }
    private void CheckLease()
    {
        var lease = _readLease();
        var now = _clock.GetUtcNow();
        if (lease.Stop || lease.RunId != _phase.RunId || lease.PhaseId != _phase.PhaseId ||
            lease.PhaseStartedAt != _phase.StartedAt || lease.SourceCommit != _source ||
            lease.DatasetSha256 != _dataset || lease.ProducerSha256 != _producer ||
            lease.ObservedAt > now || now - lease.ObservedAt > TimeSpan.FromSeconds(3) ||
            lease.ExpiresAt <= now || lease.ExpiresAt > lease.ObservedAt.AddSeconds(3))
        { throw new LoadRejectedException(LoadStopReason.Lease); }
    }
    public IDisposable EnterCleanup()
    {
        CheckCleanup();
        lock (_gate)
        {
            if (_cleanupRequests >= 100)
            { throw new LoadRejectedException(LoadStopReason.Budget); }
            _cleanupRequests++;
        }
        return new CleanupPermit();
    }
    public void CheckCleanup()
    {
        _stop.ThrowIfCancellationRequested();
        if (_clock.GetUtcNow() >= _phase.CleanupEnd ||
            _clock.GetElapsedTime(_started) >= _duration + (_phase.CleanupEnd - _phase.ActiveEnd))
        { throw new LoadRejectedException(LoadStopReason.Deadline); }
        CheckLease();
    }
    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop, cancellationToken);
        linked.CancelAfter(Remaining);
        await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check();
            lock (_gate)
            {
                if (_requests >= _phase.MaxRequests)
                { throw new LoadRejectedException(LoadStopReason.Budget); }
                Interlocked.Increment(ref _requests);
            }
            return new Slot(_slots);
        }
        catch { _slots.Release(); throw; }
    }
    public void Dispose() => _slots.Dispose();

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { slots.Release(); } }
    }
    private sealed class CleanupPermit : IDisposable { public void Dispose() { } }
}
