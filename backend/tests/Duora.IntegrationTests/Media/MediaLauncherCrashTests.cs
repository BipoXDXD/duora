extern alias worker;

using System.Diagnostics;
using Duora.Core.Media;
using worker::Duora.Worker.Media;

namespace Duora.IntegrationTests.Media;

public sealed class MediaLauncherCrashTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialTransferIsBoundedAndAnUnknownAdmissionRequiresRecovery(bool shutdown)
    {
        await using var launcher = new LauncherFixture();
        await launcher.StartAsync(Cancellation);
        launcher.Options.TotalTimeout = TimeSpan.FromSeconds(2);
        launcher.Options.CleanupTimeout = TimeSpan.FromMilliseconds(500);
        launcher.Options.ControlTimeout = TimeSpan.FromMilliseconds(100);
        launcher.SetMode("transfer-hold");
        using var sanitizer = new IsolatedPhotoSanitizer(launcher.Options);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var timer = Stopwatch.StartNew();
        var pending = sanitizer.SanitizeAsync(PhotoFormat.Jpeg, new byte[PhotoLimits.MaxBytes], stop.Token);
        await launcher.WaitForAsync("transferring", Cancellation);
        if (shutdown)
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            await Assert.ThrowsAnyAsync<IOException>(() => pending);
        }
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "Transfer and cleanup must stay bounded by the restricted candidate envelope.");
        Assert.False(launcher.Started);
        launcher.SetMode("normal");
        await launcher.WaitForAsync("transfer-ended", Cancellation);
        await Assert.ThrowsAnyAsync<IOException>(() => sanitizer.SanitizeAsync(PhotoFormat.Jpeg, TestImages.Jpeg(), Cancellation));
        Assert.False(launcher.Started);

        // A fresh Worker can reconnect; S2 still owns global admission and confirms every new result.
        launcher.Options.TotalTimeout = TimeSpan.FromSeconds(50);
        launcher.Options.CleanupTimeout = TimeSpan.FromSeconds(5);
        launcher.Options.ControlTimeout = TimeSpan.FromSeconds(2);
        using var restarted = new IsolatedPhotoSanitizer(launcher.Options);
        await restarted.EnsureReadyAsync(Cancellation);
        Assert.IsType<SanitizeOutcome.Sanitized>(
            await restarted.SanitizeAsync(PhotoFormat.Jpeg, TestImages.Jpeg(), Cancellation));
    }

    [Theory]
    [InlineData("crash")]
    [InlineData("after-output")]
    [InlineData("wrong-nonce")]
    [InlineData("wrong-hash")]
    [InlineData("wrong-version")]
    [InlineData("extra-output")]
    [InlineData("truncated")]
    [InlineData("cleanup-blocked")]
    public async Task FailedOrUnconfirmedExecutionsNeverReturnSanitizedBytes(string mode)
    {
        await using var launcher = new LauncherFixture();
        await launcher.StartAsync(Cancellation);
        launcher.SetMode(mode);
        using var sanitizer = new IsolatedPhotoSanitizer(launcher.Options);

        await Assert.ThrowsAnyAsync<IOException>(
            () => sanitizer.SanitizeAsync(PhotoFormat.Jpeg, TestImages.Jpeg(), Cancellation));
    }

    [Fact]
    public async Task ShutdownCancelsTheAdmittedAttemptAndAllowsRecoveryAfterConfirmedCleanup()
    {
        await using var launcher = new LauncherFixture();
        await launcher.StartAsync(Cancellation);
        launcher.SetMode("hold");
        using var sanitizer = new IsolatedPhotoSanitizer(launcher.Options);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var pending = sanitizer.SanitizeAsync(PhotoFormat.Jpeg, TestImages.Jpeg(), shutdown.Token);
        await launcher.WaitForAsync("started", Cancellation);
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await launcher.WaitForAsync("cleanup", Cancellation);
        launcher.SetMode("normal");

        Assert.IsType<SanitizeOutcome.Sanitized>(
            await sanitizer.SanitizeAsync(PhotoFormat.Jpeg, TestImages.Jpeg(), Cancellation));
    }

    [Fact]
    public async Task FullSlotFailsPromptlyAndDoesNotBuildAnUnboundedLocalQueue()
    {
        await using var launcher = new LauncherFixture();
        await launcher.StartAsync(Cancellation);
        launcher.SetMode("hold");
        using var sanitizer = new IsolatedPhotoSanitizer(launcher.Options);
        using var otherWorker = new IsolatedPhotoSanitizer(launcher.Options);
        var pending = sanitizer.SanitizeAsync(PhotoFormat.Jpeg, TestImages.Jpeg(), Cancellation);
        await launcher.WaitForAsync("started", Cancellation);
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => sanitizer.SanitizeAsync(PhotoFormat.Jpeg, TestImages.Jpeg(), Cancellation))
                .WaitAsync(TimeSpan.FromSeconds(3), Cancellation);
            await Assert.ThrowsAnyAsync<IOException>(() => otherWorker.SanitizeAsync(PhotoFormat.Jpeg, new byte[PhotoLimits.MaxBytes], Cancellation))
                .WaitAsync(TimeSpan.FromSeconds(3), Cancellation);
        }
        finally
        {
            launcher.SetMode("normal");
        }
        Assert.IsType<SanitizeOutcome.Sanitized>(await pending);
    }

    [Fact]
    public async Task ProfileMismatchCannotDeclareReadiness()
    {
        await using var launcher = new LauncherFixture();
        await launcher.StartAsync(Cancellation);
        launcher.Options.ProfileSha256 = new string('f', 64);
        using var sanitizer = new IsolatedPhotoSanitizer(launcher.Options);
        await Assert.ThrowsAnyAsync<IOException>(() => sanitizer.EnsureReadyAsync(Cancellation));
    }

    [Fact]
    public async Task WrongLauncherIdentityIsRejectedBeforeAnyOriginalIsSent()
    {
        await using var launcher = new LauncherFixture();
        await launcher.StartAsync(Cancellation);
        launcher.Options.LauncherUid++;
        using var sanitizer = new IsolatedPhotoSanitizer(launcher.Options);
        await Assert.ThrowsAnyAsync<IOException>(() => sanitizer.SanitizeAsync(PhotoFormat.Jpeg, TestImages.Jpeg(), Cancellation));
        Assert.False(launcher.Started);
    }

    [Fact]
    public async Task UnconfirmedCleanupDoesNotAdmitAnotherAttempt()
    {
        await using var launcher = new LauncherFixture();
        await launcher.StartAsync(Cancellation);
        launcher.SetMode("cleanup-blocked");
        using var sanitizer = new IsolatedPhotoSanitizer(launcher.Options);
        await Assert.ThrowsAnyAsync<IOException>(() => sanitizer.SanitizeAsync(PhotoFormat.Jpeg, TestImages.Jpeg(), Cancellation));
        launcher.SetMode("normal");
        await Assert.ThrowsAnyAsync<IOException>(() => sanitizer.SanitizeAsync(PhotoFormat.Jpeg, TestImages.Jpeg(), Cancellation));
        Assert.Equal(1, launcher.AdmissionCount);
    }

    [Fact]
    public async Task AValidTechnicalLimitRemainsARejectionRatherThanAnInfrastructureFailure()
    {
        await using var launcher = new LauncherFixture();
        await launcher.StartAsync(Cancellation);
        using var sanitizer = new IsolatedPhotoSanitizer(launcher.Options);
        await sanitizer.EnsureReadyAsync(Cancellation);
        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.TooManyPixels),
            await sanitizer.SanitizeAsync(PhotoFormat.Png, TestImages.DecompressionBombPng(), Cancellation));
        Assert.Equal(new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit),
            await sanitizer.SanitizeAsync(PhotoFormat.Jpeg, TestImages.ProgressiveJpegWithScans(101), Cancellation));
    }
}
