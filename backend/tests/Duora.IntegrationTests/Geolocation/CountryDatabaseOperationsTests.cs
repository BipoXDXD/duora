using System.Net;
using System.Text.Json;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Geolocation;
using Npgsql;

namespace Duora.IntegrationTests.Geolocation;

public sealed class CountryDatabaseOperationsTests(PostgresFixture postgres)
{
    private static readonly string[] SyntheticCountries = ["BR", "US", "CA"];
    private static readonly bool[] ExpectedRecoveryOrder = [false, true];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DisabledOperationsHaveNoDatabaseFilesystemOrNotificationEffects()
    {
        await using var h = await CountryDatabasePipelineTests.Harness.Create(postgres);
        var notices = new Notices();
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        await new CountryDatabaseHealthService(store, h.Validator, h.Root, notices).RunOnceAsync(Ct);
        await new CountryDatabaseRetentionService(store, h.Root).RunOnceAsync(Ct);
        Assert.Equal(CountryDatabaseRecoveryOutcome.Disabled,
            await new CountryDatabaseRecoveryService(store, h.Validator, h.Root).RecoverAsync(Ct));
        Assert.Empty(notices.Items);
        Assert.False(Directory.Exists(h.Root));
        Assert.Equal(0L, await Count(h.Source, "select count(*) from ops.country_database_state"));
    }

    [Fact]
    public async Task AgeThresholdsDeduplicateAcrossRestartRemindDailyAndNotifyRecovery()
    {
        await using var h = await Prepared();
        var notices = new Notices();
        CountryDatabaseHealthService Monitor() => new(new(h.Source, h.Time), h.Validator, h.Root, notices, enabled: true);
        var manifest = (await h.Store.ReadActiveAsync(Ct))!;
        h.Time.SetUtcNow(manifest.BuiltAt.AddHours(72).AddTicks(-1));
        await Monitor().RunOnceAsync(Ct);
        Assert.DoesNotContain(notices.Items, n => n.Code == "Age");
        h.Time.Advance(TimeSpan.FromTicks(1));
        await Monitor().RunOnceAsync(Ct);
        Assert.Single(notices.Items, n => n.Code == "Age" && n.Severity == 1);
        await Task.WhenAll(Monitor().RunOnceAsync(Ct), Monitor().RunOnceAsync(Ct));
        Assert.Single(notices.Items, n => n.Code == "Age");
        h.Time.Advance(TimeSpan.FromDays(1));
        await Monitor().RunOnceAsync(Ct);
        Assert.Equal(2, notices.Items.Count(n => n.Code == "Age"));
        h.Time.SetUtcNow(manifest.BuiltAt.AddHours(168));
        await Monitor().RunOnceAsync(Ct);
        Assert.Single(notices.Items, n => n.Code == "Age" && n.Severity == 2 && !n.Recovered);
        h.Payload = SyntheticMmdb.Create(h.Time.GetUtcNow().AddHours(-1));
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        await Monitor().RunOnceAsync(Ct);
        Assert.Single(notices.Items, n => n.Code == "Age" && n.Recovered);
    }

    [Fact]
    public async Task MissingExecutionUsesExact36HourBoundaryAndMissingBaseIsImmediate()
    {
        await using var h = await Prepared();
        var notices = new Notices();
        var monitor = new CountryDatabaseHealthService(new(h.Source, h.Time), h.Validator, h.Root, notices, enabled: true);
        h.Time.Advance(TimeSpan.FromHours(36) - TimeSpan.FromTicks(1));
        await monitor.RunOnceAsync(Ct);
        Assert.DoesNotContain(notices.Items, n => n.Code == "NoExecution");
        h.Time.Advance(TimeSpan.FromTicks(1));
        await monitor.RunOnceAsync(Ct);
        Assert.Single(notices.Items, n => n.Code == "NoExecution");
        File.Delete(Path.Combine(h.Root, (await h.Store.ReadActiveAsync(Ct))!.Sha256 + ".mmdb"));
        await monitor.RunOnceAsync(Ct);
        Assert.Single(notices.Items, n => n.Code == "MissingBase" && n.Severity == 2);
    }

    [Theory]
    [InlineData("Unauthorized")]
    [InlineData("Schema")]
    [InlineData("Integrity")]
    public async Task TerminalUpdaterFailureHasPersistentSanitizedIncident(string code)
    {
        await using var h = await Prepared();
        h.Time.Advance(TimeSpan.FromDays(1));
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.True(await h.Store.FailAsync(lease, code, false, null, Ct));
        var notices = new Notices();
        await new CountryDatabaseHealthService(new(h.Source, h.Time), h.Validator, h.Root, notices, enabled: true).RunOnceAsync(Ct);
        Assert.Single(notices.Items, n => n.Code == code && n.Severity == 2);
        await new CountryDatabaseHealthService(new(h.Source, h.Time), h.Validator, h.Root, notices, enabled: true).RunOnceAsync(Ct);
        Assert.Single(notices.Items, n => n.Code == code);
    }

    [Fact]
    public async Task ExhaustedCycleWarnsAndNotificationFailureRetriesWithStableId()
    {
        await using var h = await Prepared();
        h.Time.Advance(TimeSpan.FromDays(1));
        var first = h.Time.GetUtcNow();
        foreach (var minutes in new[] { 0, 15, 60, 240 })
        {
            h.Time.SetUtcNow(first.AddMinutes(minutes));
            var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
            Assert.True(await h.Store.FailAsync(lease, "Network", true, null, Ct));
        }
        var notices = new Notices { Fail = true };
        var monitor = new CountryDatabaseHealthService(new(h.Source, h.Time), h.Validator, h.Root, notices, enabled: true);
        await monitor.RunOnceAsync(Ct);
        Assert.Empty(notices.Items);
        notices.Fail = false;
        h.Time.Advance(TimeSpan.FromMinutes(15));
        await monitor.RunOnceAsync(Ct);
        Assert.Single(notices.Items, n => n.Code == "Exhausted");
        Assert.Equal(1L, await Count(h.Source, "select count(*) from ops.country_database_notifications where code='Exhausted'"));
    }

    [Fact]
    public async Task RecoveryUsesApprovedCopyOfflineAndNeverRejuvenatesManifest()
    {
        await using var h = await Prepared();
        var archive = new Archive();
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        await new CountryDatabaseRetentionService(store, h.Root, archive, enabled: true).RunOnceAsync(Ct);
        var before = (await h.Store.ReadActiveAsync(Ct))!;
        Assert.Single(archive.Files);
        File.Delete(Path.Combine(h.Root, before.Sha256 + ".mmdb"));
        h.Time.Advance(TimeSpan.FromHours(1));
        var recovery = new CountryDatabaseRecoveryService(store, h.Validator, h.Root, archive, enabled: true);
        Assert.Equal(CountryDatabaseRecoveryOutcome.Recovered, await recovery.RecoverAsync(Ct));
        Assert.Equal(before, await h.Store.ReadActiveAsync(Ct));
        using var active = new ActiveCountryDatabase(h.Root, h.Validator, h.Time);
        Assert.True(await active.RefreshAsync(h.Store, Ct));
        Assert.Equal("BR", active.Lookup(IPAddress.Parse("192.0.2.1")).CountryCode);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task ExpiredOrUnapprovedBackupCannotResumeDetection()
    {
        await using var h = await Prepared();
        var archive = new Archive();
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        await new CountryDatabaseRetentionService(store, h.Root, archive, enabled: true).RunOnceAsync(Ct);
        h.Time.Advance(TimeSpan.FromDays(7));
        Assert.Equal(CountryDatabaseRecoveryOutcome.Unavailable,
            await new CountryDatabaseRecoveryService(store, h.Validator, h.Root, archive, enabled: true).RecoverAsync(Ct));
        using var active = new ActiveCountryDatabase(h.Root, h.Validator, h.Time);
        Assert.False(await active.RefreshAsync(h.Store, Ct));
    }

    [Fact]
    public async Task CorruptActiveBlocksRecoveryUntilExplicitFencedHumanReview()
    {
        await using var h = await Prepared();
        var archive = new Archive();
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        await new CountryDatabaseRetentionService(store, h.Root, archive, enabled: true).RunOnceAsync(Ct);
        var manifest = (await h.Store.ReadActiveAsync(Ct))!;
        var path = Path.Combine(h.Root, manifest.Sha256 + ".mmdb");
        File.Delete(path);
        await File.WriteAllBytesAsync(path, [0, 1], Ct);
        var notices = new Notices();
        await new CountryDatabaseHealthService(store, h.Validator, h.Root, notices, enabled: true).RunOnceAsync(Ct);
        var recovery = new CountryDatabaseRecoveryService(store, h.Validator, h.Root, archive, enabled: true);
        Assert.Equal(CountryDatabaseRecoveryOutcome.ReviewRequired, await recovery.RecoverAsync(Ct));
        var generation = await Generation(h.Source);
        Assert.True(OperatorName.TryParse("synthetic-operator", out var actor));
        Assert.False(await recovery.ReleaseAsync(generation - 1, Guid.NewGuid(), actor, Ct));
        Assert.True(await recovery.ReleaseAsync(generation, Guid.NewGuid(), actor, Ct));
        Assert.Equal(CountryDatabaseRecoveryOutcome.Recovered, await recovery.RecoverAsync(Ct));
        Assert.Equal(1L, await Count(h.Source, "select count(*) from ops.country_database_notifications where code='ReviewReleased'"));
    }

    [Fact]
    public async Task ForgedManifestAndBackwardClockFailClosedAcrossServiceRestart()
    {
        await using var h = await Prepared();
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        await new CountryDatabaseHealthService(store, h.Validator, h.Root, new Notices(), enabled: true).RunOnceAsync(Ct);
        // Simulate a restored host with a clock behind the persisted high-water mark.
        var earlier = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(h.Time.GetUtcNow().AddHours(-1));
        Assert.Equal(CountryDatabaseRecoveryOutcome.ClockInvalid,
            await new CountryDatabaseRecoveryService(new(h.Source, earlier), h.Validator, h.Root, enabled: true).RecoverAsync(Ct));
        await using var q = h.MaintenanceSource.CreateCommand("update ops.country_database_versions set manifest=jsonb_set(manifest,'{License}','\"forged\"')");
        await q.ExecuteNonQueryAsync(Ct); // Migrator-only tampering fixture; app cannot mutate manifests.
        Assert.Equal(CountryDatabaseRecoveryOutcome.ReviewRequired,
            await new CountryDatabaseRecoveryService(store, h.Validator, h.Root, enabled: true).RecoverAsync(Ct));
    }

    [Fact]
    public async Task RetentionEvictsFourteenthDayActiveAndPrunesOrphansWithoutExtendingMetadata()
    {
        await using var h = await Prepared();
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        var archive = new Archive();
        var retention = new CountryDatabaseRetentionService(store, h.Root, archive, enabled: true);
        var manifest = (await h.Store.ReadActiveAsync(Ct))!;
        await retention.RunOnceAsync(Ct);
        h.Time.Advance(TimeSpan.FromDays(14) - TimeSpan.FromTicks(1));
        await retention.RunOnceAsync(Ct);
        Assert.True(File.Exists(Path.Combine(h.Root, manifest.Sha256 + ".mmdb")));
        h.Time.Advance(TimeSpan.FromTicks(1));
        Directory.CreateDirectory(Path.Combine(h.Root, ".attempt-orphan"));
        await File.WriteAllBytesAsync(Path.Combine(h.Root, new string('f', 64) + ".mmdb"), [1], Ct);
        await retention.RunOnceAsync(Ct);
        Assert.Empty(Directory.GetFileSystemEntries(h.Root));
        Assert.Empty(archive.Files);
        Assert.Null(await h.Store.ReadActiveAsync(Ct));
        Assert.Equal(1L, await Count(h.Source, "select count(*) from ops.country_database_versions"));
        h.Time.SetUtcNow(manifest.FirstDownloadedAt.AddDays(90));
        await retention.RunOnceAsync(Ct);
        Assert.Equal(0L, await Count(h.Source, "select count(*) from ops.country_database_versions"));
    }

    [Fact]
    public async Task RetentionKeepsAtMost14VersionsAndOnlyTwoPrivateCopies()
    {
        await using var h = await Prepared();
        var start = h.Time.GetUtcNow();
        for (var i = 1; i < 16; i++)
        {
            h.Time.SetUtcNow(start.AddDays(i));
            h.Payload = SyntheticMmdb.Create(h.Time.GetUtcNow().AddHours(-1));
            var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
            Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        }
        var archive = new Archive();
        await new CountryDatabaseRetentionService(new(h.MaintenanceSource, h.Time), h.Root, archive, enabled: true).RunOnceAsync(Ct);
        Assert.Equal(14, Directory.GetFiles(h.Root, "*.mmdb").Length);
        Assert.Equal(2, archive.Files.Count);
    }

    [Fact]
    public async Task UnapprovedAndTamperedArchiveCannotBecomeActive()
    {
        await using var h = await CountryDatabasePipelineTests.Harness.Create(postgres, approve: false);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Failed, await h.Service.ExecuteAsync(lease, Ct));
        var archive = new Archive();
        var m = h.Validator.Validate(h.Payload, h.Time.GetUtcNow(), Ct);
        archive.Files[m.Sha256] = h.Payload;
        var recovery = new CountryDatabaseRecoveryService(new(h.MaintenanceSource, h.Time), h.Validator, h.Root, archive, enabled: true);
        Assert.Equal(CountryDatabaseRecoveryOutcome.ReviewRequired, await recovery.RecoverAsync(Ct));
        Assert.Null(await h.Store.ReadActiveAsync(Ct));
        Assert.Empty(Directory.GetFiles(h.Root));
    }

    [Fact]
    public async Task TamperedBackupIsRejectedWithoutPublishingAndLocalReaderIsRevoked()
    {
        await using var h = await Prepared();
        var archive = new Archive();
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        await new CountryDatabaseRetentionService(store, h.Root, archive, enabled: true).RunOnceAsync(Ct);
        var m = (await h.Store.ReadActiveAsync(Ct))!;
        using var active = new ActiveCountryDatabase(h.Root, h.Validator, h.Time);
        Assert.True(await active.RefreshAsync(h.Store, Ct));
        File.Delete(Path.Combine(h.Root, m.Sha256 + ".mmdb"));
        archive.Files[m.Sha256] = [0, 1];
        Assert.Equal(CountryDatabaseRecoveryOutcome.ReviewRequired,
            await new CountryDatabaseRecoveryService(store, h.Validator, h.Root, archive, enabled: true).RecoverAsync(Ct));
        Assert.False(await active.RefreshAsync(h.Store, Ct));
        Assert.Equal(CountryLookupStatus.Unavailable, active.Lookup(IPAddress.Parse("192.0.2.1")).Status);
        Assert.Empty(Directory.GetFileSystemEntries(h.Root));
    }

    [Fact]
    public async Task MaintenanceDoesNotDeleteInFlightDownloadsAndFencesLatePublisher()
    {
        await using var h = await Prepared();
        h.Time.Advance(TimeSpan.FromDays(14));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Payload = SyntheticMmdb.Create(h.Time.GetUtcNow().AddHours(-1));
        h.Send = async (_, ct) =>
        {
            started.SetResult();
            await resume.Task.WaitAsync(ct);
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(h.Payload) };
        };
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        var pending = h.Service.ExecuteAsync(lease, Ct);
        await started.Task.WaitAsync(Ct);
        await new CountryDatabaseRetentionService(new(h.MaintenanceSource, h.Time), h.Root, enabled: true).RunOnceAsync(Ct);
        Assert.Single(Directory.GetDirectories(h.Root));
        resume.SetResult();
        Assert.Equal(CountryDatabaseUpdateOutcome.LostLease, await pending);
        Assert.Empty(Directory.GetFileSystemEntries(h.Root));
        Assert.Null(await h.Store.ReadActiveAsync(Ct));
    }

    [Fact]
    public async Task RecoveryDoesNotDowngradeToAnOlderApprovedBuildWhenNewestCopyIsMissing()
    {
        await using var h = await Prepared();
        var archive = new Archive();
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        await new CountryDatabaseRetentionService(store, h.Root, archive, enabled: true).RunOnceAsync(Ct);
        h.Time.Advance(TimeSpan.FromDays(1));
        h.Payload = SyntheticMmdb.Create(h.Time.GetUtcNow().AddHours(-1));
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        var newest = (await h.Store.ReadActiveAsync(Ct))!;
        File.Delete(Path.Combine(h.Root, newest.Sha256 + ".mmdb"));
        Assert.Equal(CountryDatabaseRecoveryOutcome.Unavailable,
            await new CountryDatabaseRecoveryService(store, h.Validator, h.Root, archive, enabled: true).RecoverAsync(Ct));
        Assert.Equal(newest, await h.Store.ReadActiveAsync(Ct));
    }

    [Fact]
    public async Task NextUpdateAfterActiveRetentionStillUsesApprovedProductBaseline()
    {
        await using var h = await Prepared();
        h.Time.Advance(TimeSpan.FromDays(14));
        await new CountryDatabaseRetentionService(new(h.MaintenanceSource, h.Time), h.Root, enabled: true).RunOnceAsync(Ct);
        h.Payload = SyntheticMmdb.Create(h.Time.GetUtcNow().AddHours(-1));
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
    }

    [Fact]
    public async Task BackwardClockDoesNotResolveAnExistingIncidentOrDeleteArtifacts()
    {
        await using var h = await Prepared();
        h.Time.Advance(TimeSpan.FromHours(72));
        var notices = new Notices();
        await new CountryDatabaseHealthService(new(h.Source, h.Time), h.Validator, h.Root, notices, enabled: true).RunOnceAsync(Ct);
        var wrongTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(h.Time.GetUtcNow().AddHours(-2));
        var wrongStore = new CountryDatabaseOperationsStore(h.MaintenanceSource, wrongTime);
        await new CountryDatabaseHealthService(wrongStore, h.Validator, h.Root, notices, enabled: true).RunOnceAsync(Ct);
        await new CountryDatabaseRetentionService(wrongStore, h.Root, enabled: true).RunOnceAsync(Ct);
        Assert.Equal(0L, await Count(h.Source,
            "select count(*) from ops.country_database_notifications where code='Age' and recovered"));
        Assert.Single(Directory.GetFiles(h.Root));
        using var restarted = new ActiveCountryDatabase(h.Root, h.Validator, wrongTime);
        Assert.False(await restarted.RefreshAsync(new(h.Source, wrongTime, TimeSpan.FromMinutes(2)), Ct));
    }

    [Fact]
    public async Task InactiveIncidentAndEventMetadataAreRemovedAt90Days()
    {
        await using var h = await Prepared();
        File.Delete(Path.Combine(h.Root, (await h.Store.ReadActiveAsync(Ct))!.Sha256 + ".mmdb"));
        await new CountryDatabaseHealthService(new(h.Source, h.Time), h.Validator, h.Root, new Notices(), enabled: true).RunOnceAsync(Ct);
        h.Time.Advance(TimeSpan.FromDays(90));
        await new CountryDatabaseRetentionService(new(h.MaintenanceSource, h.Time), h.Root, enabled: true).RunOnceAsync(Ct);
        Assert.Equal(0L, await Count(h.Source, "select count(*) from ops.country_database_incidents"));
        Assert.Equal(0L, await Count(h.Source,
            "select count(*) from ops.country_database_notifications where code='MissingBase'"));
    }

    [Fact]
    public async Task PersistedClockMarkRejectsRenewalAndPromotionFromDelayedUpdater()
    {
        await using var h = await Prepared();
        var previous = (await h.Store.ReadActiveAsync(Ct))!;
        h.Time.Advance(TimeSpan.FromDays(1));
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        var wrongTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(h.Time.GetUtcNow().AddMinutes(1));
        h.Time.Advance(TimeSpan.FromDays(1));
        await new CountryDatabaseHealthService(new(h.Source, h.Time), h.Validator, h.Root, new Notices(), enabled: true).RunOnceAsync(Ct);
        var delayed = new CountryDatabaseUpdateStore(h.Source, wrongTime, TimeSpan.FromMinutes(2));
        Assert.False(await delayed.RenewAsync(lease, Ct));
        Assert.Equal(CountryDatabasePromotion.LostLease, await delayed.PromoteAsync(lease, previous, null, Ct));
    }

    [Fact]
    public async Task PublicationGateProtectsNewStagingFromAConcurrentMaintenanceSnapshot()
    {
        await using var h = await Prepared();
        h.Time.Advance(TimeSpan.FromDays(1));
        h.Payload = SyntheticMmdb.Create(h.Time.GetUtcNow().AddHours(-1));
        var files = await new CountryDatabaseOperationsStore(h.Source, h.Time).LockFilesAsync(Ct);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        var pending = h.Service.ExecuteAsync(lease, Ct);
        var createdBeforeGate = Directory.GetDirectories(h.Root).Length;
        await files.DisposeAsync();
        await pending;
        Assert.Equal(0, createdBeforeGate);
    }

    [Fact]
    public async Task RecoveryNotificationCannotOvertakeItsUndeliveredIncident()
    {
        await using var h = await Prepared();
        var m = (await h.Store.ReadActiveAsync(Ct))!;
        h.Time.SetUtcNow(m.BuiltAt.AddHours(72));
        await using (var q = h.Source.CreateCommand("update ops.country_database_state set last_attempt_at=$1 where singleton"))
        {
            q.Parameters.AddWithValue(h.Time.GetUtcNow());
            await q.ExecuteNonQueryAsync(Ct);
        }
        var notices = new Notices { Fail = true };
        var monitor = new CountryDatabaseHealthService(new(h.Source, h.Time), h.Validator, h.Root, notices, enabled: true);
        await monitor.RunOnceAsync(Ct);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        h.Payload = SyntheticMmdb.Create(h.Time.GetUtcNow().AddHours(-1));
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        notices.Fail = false;
        await monitor.RunOnceAsync(Ct);
        var overtookIncident = notices.Items.Any(n => n.Code == "Age");
        h.Time.Advance(TimeSpan.FromMinutes(15));
        await monitor.RunOnceAsync(Ct);
        Assert.False(overtookIncident);
        Assert.Equal(ExpectedRecoveryOrder, notices.Items.Where(n => n.Code == "Age").Select(n => n.Recovered));
    }

    [Fact]
    public async Task EqualEpochCopiesIncludeActiveAndPreviousAndRecoverLastPromotion()
    {
        await using var h = await CountryDatabasePipelineTests.Harness.Create(postgres);
        var start = h.Time.GetUtcNow();
        var artifacts = SyntheticCountries.Select(country =>
        {
            var bytes = SyntheticMmdb.Create(start.AddHours(-1), country: country);
            return (Bytes: bytes, Hash: h.Validator.Validate(bytes, start, Ct).Sha256);
        }).OrderBy(x => x.Hash, StringComparer.Ordinal).ToArray();
        h.RebuildService(artifacts[0].Hash);
        for (var i = 0; i < artifacts.Length; i++)
        {
            h.Time.SetUtcNow(start.AddDays(i));
            h.Payload = artifacts[i].Bytes;
            var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
            Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        }
        var archive = new Archive();
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        await new CountryDatabaseRetentionService(store, h.Root, archive, enabled: true).RunOnceAsync(Ct);
        var copiedActive = archive.Files.ContainsKey(artifacts[2].Hash);
        var copiedPrevious = archive.Files.ContainsKey(artifacts[1].Hash);
        Assert.Equal(CountryDatabaseRecoveryOutcome.Recovered,
            await new CountryDatabaseRecoveryService(store, h.Validator, h.Root, archive, enabled: true).RecoverAsync(Ct));
        Assert.True(copiedActive);
        Assert.True(copiedPrevious);
        Assert.Equal(artifacts[2].Hash, (await h.Store.ReadActiveAsync(Ct))!.Sha256);
    }

    [Fact]
    public async Task SlowArchiveCannotPreventIndependentAgeAndExecutionAlerts()
    {
        await using var h = await Prepared();
        var archive = new Archive
        {
            PutStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinuePut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var retention = new CountryDatabaseRetentionService(new(h.MaintenanceSource, h.Time), h.Root, archive, enabled: true);
        var pending = retention.RunOnceAsync(Ct);
        await archive.PutStarted.Task.WaitAsync(Ct);
        h.Time.Advance(TimeSpan.FromHours(72));
        var notices = new Notices();
        var error = await Record.ExceptionAsync(() => new CountryDatabaseHealthService(new(h.Source, h.Time),
            h.Validator, h.Root, notices, enabled: true).RunOnceAsync(Ct));
        archive.ContinuePut.SetResult();
        await pending;
        Assert.Null(error);
        Assert.Contains(notices.Items, n => n.Code == "Age" && n.Severity == 1);
        Assert.Contains(notices.Items, n => n.Code == "NoExecution");
    }

    [Fact]
    public async Task HumanRepairAuthorizationDoesNotCarryAcrossPromotionGenerations()
    {
        await using var h = await Prepared();
        h.Time.Advance(TimeSpan.FromDays(1));
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.True(await h.Store.FailAsync(lease, "Unauthorized", false, null, Ct));
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        var archive = new Archive();
        var recovery = new CountryDatabaseRecoveryService(store, h.Validator, h.Root, archive, enabled: true);
        Assert.True(OperatorName.TryParse("synthetic-operator", out var actor));
        Assert.True(await recovery.ReleaseAsync(await Generation(h.Source), Guid.NewGuid(), actor, Ct));
        // A later normal update must not inherit the previous incident's repair authorization.
        h.Time.Advance(TimeSpan.FromDays(1));
        h.Payload = SyntheticMmdb.Create(h.Time.GetUtcNow().AddHours(-1));
        lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        await new CountryDatabaseRetentionService(store, h.Root, archive, enabled: true).RunOnceAsync(Ct);
        var m = (await h.Store.ReadActiveAsync(Ct))!;
        var path = Path.Combine(h.Root, m.Sha256 + ".mmdb");
        File.Delete(path);
        await File.WriteAllBytesAsync(path, [0, 1], Ct);
        Assert.Equal(CountryDatabaseRecoveryOutcome.ReviewRequired, await recovery.RecoverAsync(Ct));
    }

    [Theory]
    [InlineData("Source", "https://invalid.example/country.mmdb")]
    [InlineData("DatabaseType", "../invalid")]
    [InlineData("FirstDownloadedAt", "2026-10-21T10:00:00+00:00")]
    public async Task InvalidManifestNeverLooksHealthy(string field, string value)
    {
        await using var h = await Prepared();
        await using (var q = h.MaintenanceSource.CreateCommand(
            "update ops.country_database_versions set manifest=jsonb_set(manifest,array[$1],$2::jsonb)"))
        {
            q.Parameters.AddWithValue(field);
            q.Parameters.AddWithValue(JsonSerializer.Serialize(value));
            await q.ExecuteNonQueryAsync(Ct);
        }
        var notices = new Notices();
        var store = new CountryDatabaseOperationsStore(h.MaintenanceSource, h.Time);
        await new CountryDatabaseHealthService(store, h.Validator, h.Root, notices, enabled: true).RunOnceAsync(Ct);
        Assert.Contains(notices.Items, n => n.Code == "Integrity" && n.Severity == 2);
        Assert.Equal(CountryDatabaseRecoveryOutcome.ReviewRequired,
            await new CountryDatabaseRecoveryService(store, h.Validator, h.Root, enabled: true).RecoverAsync(Ct));
    }

    private async Task<CountryDatabasePipelineTests.Harness> Prepared()
    {
        var h = await CountryDatabasePipelineTests.Harness.Create(postgres);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabaseUpdateOutcome.Promoted, await h.Service.ExecuteAsync(lease, Ct));
        return h;
    }

    private static async Task<long> Count(NpgsqlDataSource source, string sql)
    {
        await using var q = source.CreateCommand(sql);
        return (long)(await q.ExecuteScalarAsync(Ct))!;
    }
    private static Task<long> Generation(NpgsqlDataSource source) =>
        Count(source, "select active_fencing_token from ops.country_database_state where singleton");

    private sealed class Notices : ICountryDatabaseNotifier
    {
        public List<CountryDatabaseNotification> Items { get; } = [];
        public bool Fail { get; set; }
        public Task SendAsync(CountryDatabaseNotification notification, CancellationToken ct)
        {
            if (Fail)
            {
                throw new IOException("Synthetic transport failure; never persist exception text.");
            }
            lock (Items)
            {
                if (Items.All(n => n.Id != notification.Id))
                {
                    Items.Add(notification);
                }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class Archive : ICountryDatabaseArchive
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Files.Keys.ToArray());
        public TaskCompletionSource? PutStarted { get; init; }
        public TaskCompletionSource? ContinuePut { get; init; }
        public async Task PutAsync(CountryDatabaseManifest manifest, Stream content, CancellationToken ct)
        {
            PutStarted?.TrySetResult();
            if (ContinuePut is not null)
            { await ContinuePut.Task.WaitAsync(ct); }
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            Files.TryAdd(manifest.Sha256, copy.ToArray());
        }
        public Task<Stream?> OpenAsync(string hash, CancellationToken ct) =>
            Task.FromResult<Stream?>(Files.TryGetValue(hash, out var bytes) ? new MemoryStream(bytes, writable: false) : null);
        public Task DeleteAsync(string hash, CancellationToken ct)
        {
            Files.Remove(hash);
            return Task.CompletedTask;
        }
    }
}
