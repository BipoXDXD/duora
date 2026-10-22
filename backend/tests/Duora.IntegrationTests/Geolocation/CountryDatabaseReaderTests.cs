using System.Net;
using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Geolocation;

public sealed class CountryDatabaseReaderTests
{
    private static readonly string[] SyntheticCountries = ["BR", "US"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SyntheticFixtureIsReadableByTheApprovedLibrary()
    {
        using var stream = new MemoryStream(SyntheticMmdb.Create(Now.AddHours(-1)));
        using var reader = new MaxMind.Db.Reader(stream);
        Assert.Equal(SyntheticMmdb.DatabaseType, reader.Metadata.DatabaseType);
        Assert.Equal("BR", reader.Find<Dictionary<string, object>>(IPAddress.Parse("192.0.2.1"))!["country_code"]);
        Assert.Equal(3, reader.FindAll<Dictionary<string, object>>(cacheSize: 16).Count());
    }

    [Fact]
    public void RealReaderUsesSyntheticIpv4AndIpv6AndRechecksAgeOnEveryLookup()
    {
        using var h = new Harness();
        var bytes = SyntheticMmdb.Create(Now.AddHours(-1));
        var manifest = h.Validator.Validate(bytes, Now, Ct);
        h.Save(manifest, bytes);
        Assert.True(h.Active.TryReplace(manifest, Ct));
        Assert.Equal(new(CountryLookupStatus.Detected, "BR"), h.Active.Lookup(IPAddress.Parse("192.0.2.1")));
        Assert.Equal(new(CountryLookupStatus.Detected, "BR"), h.Active.Lookup(IPAddress.Parse("2001:db8::1")));
        Assert.Equal(new(CountryLookupStatus.Detected, "US"), h.Active.Lookup(IPAddress.Parse("203.0.113.1")));
        Assert.Equal(CountryLookupStatus.Unknown, h.Active.Lookup(IPAddress.Parse("198.51.100.1")).Status);
        h.Time.Advance(TimeSpan.FromHours(167));
        Assert.Equal(CountryLookupStatus.Unavailable, h.Active.Lookup(IPAddress.Parse("192.0.2.1")).Status);
    }

    [Theory]
    [InlineData("synthetic-ipinfo-country", "BR", false)]
    [InlineData(SyntheticMmdb.DatabaseType, "BR", true)]
    public void ProductOrSchemaSubstitutionIsRejected(string type, string country, bool unknownField)
    {
        using var h = new Harness();
        var error = Assert.Throws<CountryDatabaseValidationException>(() =>
            h.Validator.Validate(SyntheticMmdb.Create(Now.AddHours(-1), type, country, unknownField), Now, Ct));
        Assert.Equal("Schema", error.Code);
    }

    [Fact]
    public void InvalidCodeIsRejectedAndPublicationCannotBeForgedOrRenewed()
    {
        using var h = new Harness();
        Assert.Equal("Schema", Assert.Throws<CountryDatabaseValidationException>(() =>
            h.Validator.Validate(SyntheticMmdb.Create(Now.AddHours(-1), country: "ZZ"), Now, Ct)).Code);
        var bytes = SyntheticMmdb.Create(Now.AddHours(-1));
        var manifest = h.Validator.Validate(bytes, Now, Ct);
        h.Save(manifest, bytes);
        Assert.True(h.Active.TryReplace(manifest, Ct));
        Assert.Equal(CountryLookupStatus.Detected, h.Active.Lookup(IPAddress.Parse("192.0.2.1")).Status);
        Assert.False(h.Active.TryReplace(manifest with { BuiltAt = Now }, Ct));
        Assert.Throws<CountryDatabaseValidationException>(() => h.Validator.Validate(SyntheticMmdb.Create(Now.AddSeconds(1)), Now, Ct));
        Assert.Throws<CountryDatabaseValidationException>(() => h.Validator.Validate(SyntheticMmdb.Create(Now.AddDays(-7)), Now, Ct));
    }

    [Fact]
    public void DelayedCommittedSnapshotCannotReplaceANewerGenerationEvenAtTheSameEpoch()
    {
        using var h = new Harness();
        var aBytes = SyntheticMmdb.Create(Now.AddHours(-1));
        var a = h.Validator.Validate(aBytes, Now, Ct);
        var bBytes = SyntheticMmdb.Create(Now.AddHours(-1), country: "US");
        var b = h.Validator.Validate(bBytes, Now, Ct);
        h.Save(a, aBytes);
        h.Save(b, bBytes);
        Assert.True(h.Active.TryReplace(new CountryDatabaseSnapshot(2, b), Ct));
        Assert.False(h.Active.TryReplace(new CountryDatabaseSnapshot(1, a), Ct));
        Assert.Equal("US", h.Active.Lookup(IPAddress.Parse("192.0.2.1")).CountryCode);
    }

    [Fact]
    public void CorruptionOrMissingFileKeepsPreviousValidReaderButCannotStartOne()
    {
        using var h = new Harness();
        var bytes = SyntheticMmdb.Create(Now.AddHours(-1));
        var m = h.Validator.Validate(bytes, Now, Ct);
        Assert.False(h.Active.TryReplace(m, Ct));
        Assert.Equal(CountryLookupStatus.Unavailable, h.Active.Lookup(IPAddress.Loopback).Status);
        h.Save(m, bytes);
        Assert.True(h.Active.TryReplace(m, Ct));
        File.WriteAllBytes(Path.Combine(h.Root, m.Sha256 + ".mmdb"), [0, 1]);
        Assert.False(h.Active.TryReplace(m, Ct));
        Assert.Equal(CountryLookupStatus.Detected, h.Active.Lookup(IPAddress.Parse("192.0.2.1")).Status);
        using var restarted = new ActiveCountryDatabase(h.Root, h.Validator, h.Time);
        Assert.False(restarted.TryReplace(m, Ct));
        Assert.Equal(CountryLookupStatus.Unavailable, restarted.Lookup(IPAddress.Loopback).Status);
        Assert.Throws<CountryDatabaseValidationException>(() => h.Validator.Validate([0, 1], Now, Ct));
    }

    [Fact]
    public async Task ConcurrentSwapsAndReadsNeverDisposeAnInFlightReader()
    {
        using var h = new Harness();
        var aBytes = SyntheticMmdb.Create(Now.AddHours(-1));
        var a = h.Validator.Validate(aBytes, Now, Ct);
        var bBytes = SyntheticMmdb.Create(Now.AddHours(-1), country: "US");
        var b = h.Validator.Validate(bBytes, Now, Ct);
        h.Save(a, aBytes);
        h.Save(b, bBytes);
        Assert.True(h.Active.TryReplace(a, Ct));
        using var start = new Barrier(3);
        var reads = Task.Run(() => { start.SignalAndWait(Ct); for (var i = 0; i < 500; i++) { Assert.Contains(h.Active.Lookup(IPAddress.Parse("192.0.2.1")).CountryCode, SyntheticCountries); } }, Ct);
        var swaps = Task.Run(() => { start.SignalAndWait(Ct); for (var i = 0; i < 50; i++) { Assert.True(h.Active.TryReplace(i % 2 == 0 ? b : a, Ct)); } }, Ct);
        start.SignalAndWait(Ct);
        await Task.WhenAll(reads, swaps);
    }

    [Fact]
    public void RewoundClockCannotReactivateAnExpiredReaderOrReloadItsManifest()
    {
        using var h = new Harness();
        var clock = new RewindableClock { Now = Now };
        var validator = new CountryDatabaseArtifactValidator(new(SyntheticMmdb.DatabaseType, 1024 * 1024, 100), clock);
        using var active = new ActiveCountryDatabase(h.Root, validator, clock);
        var bytes = SyntheticMmdb.Create(Now.AddHours(-1));
        var manifest = validator.Validate(bytes, Now, Ct);
        h.Save(manifest, bytes);
        Assert.True(active.TryReplace(manifest, Ct));
        clock.Now = Now.AddHours(167);
        Assert.Equal(CountryLookupStatus.Unavailable, active.Lookup(IPAddress.Parse("192.0.2.1")).Status);
        clock.Now = Now.AddHours(166);
        Assert.Equal(CountryLookupStatus.Unavailable, active.Lookup(IPAddress.Parse("192.0.2.1")).Status);
        Assert.False(active.TryReplace(manifest, Ct));
    }

    private sealed class RewindableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "duora-mmdb-" + Guid.NewGuid().ToString("N"));
        public FakeTimeProvider Time { get; } = new(Now);
        public CountryDatabaseArtifactValidator Validator { get; }
        public ActiveCountryDatabase Active { get; }
        public Harness()
        {
            Directory.CreateDirectory(Root);
            Validator = new(new(SyntheticMmdb.DatabaseType, 1024 * 1024, 100), Time);
            Active = new(Root, Validator, Time);
        }
        public void Save(CountryDatabaseManifest m, byte[] bytes) => File.WriteAllBytes(Path.Combine(Root, m.Sha256 + ".mmdb"), bytes);
        public void Dispose() { Active.Dispose(); Directory.Delete(Root, true); }
    }
}
