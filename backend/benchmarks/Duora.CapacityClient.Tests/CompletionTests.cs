namespace Duora.CapacityClient.Tests;

public sealed class CompletionTests
{
    [Fact]
    public void DeadlineWithAnUnauthenticatedOrIdleParticipantIsNotCompletion()
    {
        var phase = new LoadPhase("steady-1", 2, 1800, false);
        var participants = new[] { Complete(), Complete() with { Ticks = 0 } };
        Assert.False(PhaseOutcome.AcceptParticipants(phase, "http-read", participants));
        participants[1] = Complete() with { CoveredMilliseconds = 1000 };
        Assert.False(PhaseOutcome.AcceptParticipants(phase, "http-read", participants));
        participants[1] = Complete() with { IdentityVerified = false };
        Assert.False(PhaseOutcome.AcceptParticipants(phase, "http-read", participants));
    }

    [Fact]
    public void NotificationsAndReconnectMustBeProvenForEachRequiredParticipant()
    {
        var phase = new LoadPhase("reconnect-1-1", 2, 10, true);
        var participants = new[] { Complete() with { Reconnected = true }, Complete() with { Notifications = 0 } };
        Assert.False(PhaseOutcome.AcceptParticipants(phase, "signalr-invalidation", participants));
        participants[1] = Complete();
        Assert.True(PhaseOutcome.AcceptParticipants(phase, "signalr-invalidation", participants));
        participants[1] = Complete() with { InvalidNotification = true };
        Assert.False(PhaseOutcome.AcceptParticipants(phase, "signalr-invalidation", participants));
        Assert.False(PhaseOutcome.AcceptParticipants(phase, "http-read", participants));
    }

    [Fact]
    public void AcknowledgedLoginRequiresLogoutEvenIfMeValidationFails()
    {
        var login = new LoginLifecycle();
        login.Started();
        login.Acknowledged();
        Assert.False(login.RequiresLogout);
        Assert.False(login.CleanupKnown);
        Assert.Throws<LoadRejectedException>(login.Closed);
        login.IdentityVerified();
        Assert.True(login.RequiresLogout);
        login.Closed();
        Assert.True(login.CleanupKnown);
    }

    [Fact]
    public void UncertainLoginCannotBecomeConfirmedCleanupWithoutAnAcknowledgement()
    {
        var login = new LoginLifecycle();
        login.Started();
        Assert.False(login.RequiresLogout);
        Assert.False(login.CleanupKnown);
        Assert.Throws<LoadRejectedException>(login.Closed);
    }

    private static ParticipantReceipt Complete() => new(true, true, 1, true, false, 1, 0, 0, 0, 1800000);
}
