namespace Duora.CapacityClient;

public sealed record ParticipantReceipt(bool LoginAcknowledged, bool IdentityVerified, long Ticks,
    bool HubConnected, bool Reconnected, long Notifications, long Gaps, long Duplicates, long OutOfOrder,
    long CoveredMilliseconds = 0, bool InvalidNotification = false);

public static class PhaseOutcome
{
    public static bool AcceptParticipants(LoadPhase phase, string coverage, IReadOnlyCollection<ParticipantReceipt> participants)
    {
        if (participants.Count != phase.Users || participants.Any(p => !p.LoginAcknowledged || !p.IdentityVerified || p.Ticks == 0 ||
            p.CoveredMilliseconds < phase.Seconds * 900L))
        { return false; }
        if (phase.Reconnect && (coverage != "signalr-invalidation" || participants.Count(p => p.Reconnected) != (phase.Users + 1) / 2))
        { return false; }
        return coverage != "signalr-invalidation" || participants.All(p => p.HubConnected && p.Notifications > 0 && !p.InvalidNotification &&
            p.Gaps == 0 && p.Duplicates == 0 && p.OutOfOrder == 0);
    }
}

public sealed class LoginLifecycle
{
    private int _state;
    public bool RequiresLogout => _state == 3;
    public bool CleanupKnown => _state is 0 or 4;
    public bool LoginAcknowledged => _state >= 2;
    public void Started()
    {
        if (_state != 0)
        { throw new LoadRejectedException(LoadStopReason.Authentication); }
        _state = 1;
    }
    public void Acknowledged()
    {
        if (_state != 1)
        { throw new LoadRejectedException(LoadStopReason.Authentication); }
        _state = 2;
    }
    public void Closed()
    {
        if (_state != 3)
        { throw new LoadRejectedException(LoadStopReason.Cleanup); }
        _state = 4;
    }
    public void IdentityVerified()
    {
        if (_state != 2)
        { throw new LoadRejectedException(LoadStopReason.Authentication); }
        _state = 3;
    }
}
