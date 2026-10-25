using System.Text.Json;

namespace Duora.CapacityClient;

public static class CoverageGaps
{
    public static IReadOnlyList<string> Required { get; } = ["messaging-transactions", "licensed-corpus18-release",
        "full-diff-WAL-restic-overlap", "isolated-restore-RPO-RTO", "real-cgroup-ancestry-and-isolation"];
}

public sealed record PhaseReceipt(string Phase, int Users, long StartedRequests, long Ticks,
    long Received, long Gaps, long Duplicates, long OutOfOrder, bool Completed, bool CleanupConfirmed, string Reason);

public static class Capabilities
{
    public static void Validate(CapabilityBinding capability, LoadProfile profile)
    {
        if (!capability.OperatorQualified || capability.RunId != profile.Target!.RunId ||
            capability.SourceCommit != profile.SourceCommit || capability.DatasetSha256 != profile.Dataset!.Sha256)
        { throw new LoadRejectedException(LoadStopReason.Capability); }
        using var openApi = JsonDocument.Parse(PrivateFiles.ReadPinned(capability.OpenApi));
        var paths = openApi.RootElement.GetProperty("paths");
        foreach (var entry in new[] { ("/api/v1/auth/csrf", "get"), ("/api/v1/auth/login", "post"),
            ("/api/v1/auth/logout", "post"), ("/api/v1/me", "get"), ("/api/v1/events", "get"), ("/api/v1/rooms/{roomId}", "get") })
        {
            if (!paths.TryGetProperty(entry.Item1, out var path) || !path.TryGetProperty(entry.Item2, out _))
            { throw new LoadRejectedException(LoadStopReason.Capability); }
        }
        if (profile.Coverage == "signalr-invalidation" &&
            (!SafeHubPath(capability.HubPath) || !SafeName(capability.Notification) || !SafeName(capability.SequenceProperty) ||
             !Pins.IsHex(capability.SignalRContractReceiptSha256, 64)))
        { throw new LoadRejectedException(LoadStopReason.Capability); }
        if (profile.Coverage == "http-read" && (capability.HubPath is not null || capability.Notification is not null ||
            capability.SequenceProperty is not null || capability.SignalRContractReceiptSha256 is not null))
        { throw new LoadRejectedException(LoadStopReason.Capability); }
    }

    private static bool SafeName(string? value) => value is { Length: >= 1 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static bool SafeHubPath(string? path) => path is { Length: > 6 and <= 96 } &&
        path.StartsWith("/hubs/", StringComparison.Ordinal) && path[6..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

/// <summary>Each phase has a fixed wall/monotonic deadline and its own bounded teardown reserve.</summary>
public static class LoadRunner
{
    public static async Task<IReadOnlyList<PhaseReceipt>> RunAsync(LoadProfile profile, CancellationToken stop)
    {
        profile.Validate(DateTimeOffset.UtcNow);
        var dataset = PrivateFiles.ReadJson<SyntheticDataset>(profile.Dataset!);
        Pins.ValidateDataset(dataset, profile);
        var capability = PrivateFiles.ReadJson<CapabilityBinding>(profile.Capability!);
        Capabilities.Validate(capability, profile);
        var receipts = new List<PhaseReceipt>();
        var start = profile.StartedAt!.Value;
        foreach (var phase in profile.Phases!)
        {
            stop.ThrowIfCancellationRequested();
            var delay = start - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            { await Task.Delay(delay, stop).ConfigureAwait(false); }
            var spec = new PhaseSpec(profile.Target!.RunId, phase.Name, start, start.AddSeconds(phase.Seconds),
                start.AddSeconds(phase.Seconds + 120), profile.MaxRequestsPerPhase, profile.MaxInFlight);
            var receipt = await RunPhaseAsync(profile, dataset, capability, phase, spec, stop).ConfigureAwait(false);
            receipts.Add(receipt);
            if (!receipt.Completed || !receipt.CleanupConfirmed)
            { break; }
            start = spec.CleanupEnd;
        }
        return receipts;
    }

    private static async Task<PhaseReceipt> RunPhaseAsync(LoadProfile profile, SyntheticDataset dataset, CapabilityBinding capability,
        LoadPhase phase, PhaseSpec spec, CancellationToken stop)
    {
        if (phase.Reconnect && profile.Coverage != "signalr-invalidation")
        { return new PhaseReceipt(phase.Name, phase.Users, 0, 0, 0, 0, 0, 0, false, true, "reconnect_capability_missing"); }
        using var budget = new PhaseBudget(spec, profile.SourceCommit!, profile.Dataset!.Sha256, profile.LeaseProducerSha256!,
            () => JsonSerializer.Deserialize<GuardLease>(PrivateFiles.Read(profile.LeasePath!, 8192), PrivateFiles.JsonOptions)
                ?? throw new LoadRejectedException(LoadStopReason.Lease), TimeProvider.System, stop);
        budget.Check();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(stop);
        active.CancelAfter(budget.Remaining);
        var sessions = new List<OfficialSession>();
        long ticks = 0;
        string reason = "completed";
        bool completed = false;
        var watchdog = LeaseWatch.RunAsync(budget.Check, active);
        try
        {
            for (var user = 0; user < phase.Users; user++)
            { sessions.Add(new OfficialSession(profile, dataset.Users[user], capability, budget)); }
            var work = sessions.Select(async (session, index) =>
            {
                try
                {
                    await session.AuthenticateAsync(active.Token).ConfigureAwait(false);
                    if (profile.Coverage == "signalr-invalidation")
                    {
                        await session.ConnectHubAsync(active.Token).ConfigureAwait(false);
                        if (phase.Reconnect && index % 2 == 0)
                        { await session.ReconnectAsync(active.Token).ConfigureAwait(false); }
                    }
                    while (!active.IsCancellationRequested)
                    {
                        await session.TickAsync(active.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref ticks);
                        await Task.Delay(profile.TickMilliseconds, active.Token).ConfigureAwait(false);
                    }
                }
                catch { await active.CancelAsync().ConfigureAwait(false); throw; }
            }).ToArray();
            try
            { await Task.WhenAll(work).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested && DateTimeOffset.UtcNow >= spec.ActiveEnd)
            { completed = true; }
        }
        catch (LoadRejectedException error) { reason = error.Reason.ToString(); }
        catch (OperationCanceledException) { reason = "cancelled"; }
        catch (Exception) { reason = "transport"; }
        finally { await active.CancelAsync().ConfigureAwait(false); }
        var watchReason = await watchdog.ConfigureAwait(false);
        if (watchReason is not null && watchReason != LoadStopReason.Deadline)
        { reason = watchReason.ToString()!; completed = false; }
        var cleanup = true;
        using var cleanupToken = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var cleanupRemaining = spec.CleanupEnd - DateTimeOffset.UtcNow;
        cleanupToken.CancelAfter(cleanupRemaining > TimeSpan.Zero ? cleanupRemaining : TimeSpan.Zero);
        var cleanupWatch = LeaseWatch.RunAsync(budget.CheckCleanup, cleanupToken);
        foreach (var session in sessions)
        {
            try
            { await session.LogoutAsync(cleanupToken.Token).ConfigureAwait(false); }
            catch (Exception) { cleanup = false; }
            try
            { await session.DisposeAsync().AsTask().WaitAsync(cleanupToken.Token).ConfigureAwait(false); }
            catch (Exception) { cleanup = false; }
        }
        await cleanupToken.CancelAsync().ConfigureAwait(false);
        if (await cleanupWatch.ConfigureAwait(false) is not null)
        { cleanup = false; }
        if (completed && !PhaseOutcome.AcceptParticipants(phase, profile.Coverage!, sessions.Select(s => s.Receipt()).ToArray()))
        { completed = false; reason = "participant_coverage_incomplete"; }
        if (!cleanup)
        { reason = "cleanup_unknown"; completed = false; }
        var received = sessions.Sum(x => x.Sequence.Received);
        var gaps = sessions.Sum(x => x.Sequence.Gaps);
        var duplicates = sessions.Sum(x => x.Sequence.Duplicates);
        var disorder = sessions.Sum(x => x.Sequence.OutOfOrder);
        if (profile.Coverage == "signalr-invalidation" && (received == 0 || gaps > 0 || duplicates > 0 || disorder > 0))
        { completed = false; reason = "sequence_incomplete"; }
        return new PhaseReceipt(phase.Name, phase.Users, budget.StartedRequests, ticks, received, gaps, duplicates, disorder, completed, cleanup, reason);
    }

}
