using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Geolocation;

public sealed class CountryDatabaseUpdateTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly int[] RetryMinutes = [0, 15, 60, 240];
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TwoWorkersCannotOwnOneCycleAndExpiredWorkerCannotPromote()
    {
        await using var h = await Harness.Create(postgres);
        var claims = await Task.WhenAll(h.Store.ClaimAsync(Ct), h.Store.ClaimAsync(Ct));
        var old = Assert.Single(claims.OfType<CountryDatabaseUpdateLease>());
        h.Time.Advance(LeaseDuration);
        var current = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.True(current.Token > old.Token);
        var manifest = Manifest('a', h.Time.GetUtcNow());
        Assert.False(await h.Store.RenewAsync(old, Ct));
        Assert.Equal(CountryDatabasePromotion.LostLease, await h.Store.PromoteAsync(old, manifest, manifest.Sha256, Ct));
        Assert.Null(await h.Store.ReadActiveAsync(Ct));
        Assert.Equal(CountryDatabasePromotion.Promoted, await h.Store.PromoteAsync(current, manifest, manifest.Sha256, Ct));
        Assert.Equal(manifest, await h.Store.ReadActiveAsync(Ct));
    }

    [Fact]
    public async Task FirstPromotionRequiresExplicitDigestApprovalAndDoesNotChangeActiveOnRefusal()
    {
        await using var h = await Harness.Create(postgres);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        var manifest = Manifest('b', Now);
        Assert.Equal(CountryDatabasePromotion.RequiresApproval, await h.Store.PromoteAsync(lease, manifest, null, Ct));
        Assert.Null(await h.Store.ReadActiveAsync(Ct));
        Assert.Equal(CountryDatabasePromotion.Promoted, await h.Store.PromoteAsync(lease, manifest, manifest.Sha256, Ct));
        Assert.Null(await h.Store.ClaimAsync(Ct));
    }

    [Fact]
    public async Task LegacyManifestCannotBeAdmittedEvenWithApprovedDigest()
    {
        await using var h = await Harness.Create(postgres);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        var legacy = Manifest('b', Now) with
        {
            Product = "country.mmdb", Schema = "ipinfo-country-v1", Source = "https://ipinfo.io/data/free/country.mmdb",
        };
        Assert.Equal(CountryDatabasePromotion.InvalidManifest, await h.Store.PromoteAsync(lease, legacy, legacy.Sha256, Ct));
        Assert.Null(await h.Store.RecordCandidateAsync(lease, legacy, Ct));
        Assert.Null(await h.Store.ReadActiveAsync(Ct));
    }

    [Fact]
    public async Task RetryTimesAreDurableBoundedAndHonorRetryAfter()
    {
        await using var h = await Harness.Create(postgres);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.True(await h.Store.FailAsync(lease, "RateLimited", true, Now.AddMinutes(30), Ct));
        h.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Null(await h.Store.ClaimAsync(Ct));
        h.Time.Advance(TimeSpan.FromMinutes(15));
        var restarted = new CountryDatabaseUpdateStore(h.Source, h.Time, LeaseDuration);
        lease = Assert.IsType<CountryDatabaseUpdateLease>(await restarted.ClaimAsync(Ct));
        Assert.Equal(2, lease.Attempt);
        Assert.True(await restarted.FailAsync(lease, "Network", true, null, Ct));
        h.Time.SetUtcNow(Now.AddHours(1));
        lease = Assert.IsType<CountryDatabaseUpdateLease>(await restarted.ClaimAsync(Ct));
        Assert.Equal(3, lease.Attempt);
        Assert.True(await restarted.FailAsync(lease, "Network", true, null, Ct));
        h.Time.SetUtcNow(Now.AddHours(4));
        lease = Assert.IsType<CountryDatabaseUpdateLease>(await restarted.ClaimAsync(Ct));
        Assert.Equal(4, lease.Attempt);
        Assert.True(await restarted.FailAsync(lease, "Network", true, null, Ct));
        Assert.Null(await restarted.ClaimAsync(Ct));
        h.Time.SetUtcNow(Now.AddDays(1));
        Assert.Equal(1, Assert.IsType<CountryDatabaseUpdateLease>(await restarted.ClaimAsync(Ct)).Attempt);
    }

    [Fact]
    public async Task TerminalFailuresBlockSubsequentCyclesAndStaleFailureCannotBlockNewOwner()
    {
        await using var h = await Harness.Create(postgres);
        var old = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        h.Time.Advance(LeaseDuration);
        var current = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.False(await h.Store.FailAsync(old, "Schema", false, null, Ct));
        Assert.True(await h.Store.FailAsync(current, "Unauthorized", false, null, Ct));
        h.Time.Advance(TimeSpan.FromDays(3));
        Assert.Null(await h.Store.ClaimAsync(Ct));
    }

    [Fact]
    public async Task CatchUpUsesLatestDayAndRenewalPreservesExclusiveOwnership()
    {
        await using var h = await Harness.Create(postgres);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await h.Store.RenewAsync(lease, Ct));
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await h.Store.ClaimAsync(Ct));
        var first = Manifest('c', h.Time.GetUtcNow());
        Assert.Equal(CountryDatabasePromotion.Promoted, await h.Store.PromoteAsync(lease, first, first.Sha256, Ct));
        h.Time.Advance(TimeSpan.FromDays(3));
        lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(Now.AddDays(3), lease.DueAt);
        // Same digest keeps original first-download metadata; downloading does not renew age or retention.
        var redownload = first with { FirstDownloadedAt = h.Time.GetUtcNow(), ValidatedAt = h.Time.GetUtcNow() };
        Assert.Equal(CountryDatabasePromotion.Unchanged, await h.Store.PromoteAsync(lease, redownload, null, Ct));
        Assert.Equal(first, await h.Store.ReadActiveAsync(Ct));
    }

    [Fact]
    public async Task InvalidOrRegressedCandidateCannotReplaceActive()
    {
        await using var h = await Harness.Create(postgres);
        var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        var first = Manifest('d', Now);
        Assert.Equal(CountryDatabasePromotion.Promoted, await h.Store.PromoteAsync(lease, first, first.Sha256, Ct));
        h.Time.Advance(TimeSpan.FromDays(1));
        lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.Equal(CountryDatabasePromotion.InvalidManifest, await h.Store.PromoteAsync(lease, Manifest('e', h.Time.GetUtcNow()) with { BuiltAt = Now.AddDays(-8) }, null, Ct));
        Assert.Equal(CountryDatabasePromotion.RequiresApproval, await h.Store.PromoteAsync(lease, Manifest('e', h.Time.GetUtcNow()) with { DatabaseType = "another-product" }, null, Ct));
        Assert.Equal(CountryDatabasePromotion.RequiresApproval, await h.Store.PromoteAsync(lease, Manifest('e', h.Time.GetUtcNow()) with { BuiltAt = Now.AddHours(-3) }, null, Ct));
        Assert.Equal(first, await h.Store.ReadActiveAsync(Ct));
    }

    [Fact]
    public async Task ExistingConsumersCannotClaimTheCountryDatabaseKind()
    {
        await using var h = await Harness.Create(postgres);
        var first = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
        Assert.True(await h.Store.FailAsync(first, "Network", true, null, Ct));
        h.Time.Advance(TimeSpan.FromMinutes(15));
        var participation = new Duora.Infrastructure.Participation.ParticipationService(h.Source, h.Time);
        Assert.Null(await participation.ClaimJobAsync(Ct));
        var retention = new Duora.Infrastructure.Operations.RetentionService(h.Source, h.Time);
        Assert.NotEqual(first.Id, Assert.IsType<Duora.Infrastructure.Operations.RetentionJob>(await retention.ClaimAsync(Ct)).Id);
        Assert.Equal(first.Id, Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct)).Id);
    }

    [Fact]
    public async Task FinalAttemptRetryAfterSurvivesRestartAndDailyRollover()
    {
        await using var h = await Harness.Create(postgres);
        foreach (var minutes in RetryMinutes)
        {
            h.Time.SetUtcNow(Now.AddMinutes(minutes));
            var lease = Assert.IsType<CountryDatabaseUpdateLease>(await h.Store.ClaimAsync(Ct));
            Assert.True(await h.Store.FailAsync(lease, "RateLimited", true, minutes == 240 ? Now.AddDays(3) : null, Ct));
        }
        h.Time.SetUtcNow(Now.AddDays(1));
        var restarted = new CountryDatabaseUpdateStore(h.Source, h.Time, LeaseDuration);
        Assert.Null(await restarted.ClaimAsync(Ct));
        h.Time.SetUtcNow(Now.AddDays(3));
        Assert.Equal(1, Assert.IsType<CountryDatabaseUpdateLease>(await restarted.ClaimAsync(Ct)).Attempt);
    }

    internal static CountryDatabaseManifest Manifest(char hash, DateTimeOffset now) =>
        new(new string(hash, 64), 100, Now.AddHours(-1), now, now, SyntheticMmdb.DatabaseType,
            "ipinfo_lite.mmdb", "ipinfo-lite-country-v1", "CC-BY-SA-4.0", "https://ipinfo.io/data/ipinfo_lite.mmdb");

    private sealed class Harness(NpgsqlDataSource source, FakeTimeProvider time) : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; } = source;
        public FakeTimeProvider Time { get; } = time;
        public CountryDatabaseUpdateStore Store { get; } = new(source, time, LeaseDuration);
        public static async Task<Harness> Create(PostgresFixture postgres) =>
            new(NpgsqlDataSource.Create(await postgres.CreateMigratedDatabaseAsync(Ct)), new FakeTimeProvider(Now));
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }
}
