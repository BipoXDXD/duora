using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Geolocation;

public sealed class IpinfoLiteContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LiteCountryCodeIsUsedForIpv4Ipv6AndMappedAddressesWithoutProjectingAsn()
    {
        using var h = new Harness();
        // A misleading country name must never become the ISO code.
        var bytes = SyntheticMmdb.CreateLite(Now.AddHours(-1), configure: map => map["country"] = "US");
        var manifest = h.Validator.Validate(bytes, Now, Ct);
        Assert.Equal("ipinfo_lite.mmdb", manifest.Product);
        Assert.Equal("ipinfo-lite-country-v1", manifest.Schema);
        Assert.Equal("https://ipinfo.io/data/ipinfo_lite.mmdb", manifest.Source);
        Assert.Equal("CC-BY-SA-4.0", manifest.License);
        h.Save(manifest, bytes);
        Assert.True(h.Active.TryReplace(manifest, Ct));
        foreach (var address in new[] { "192.0.2.1", "2001:db8::1", "::ffff:192.0.2.1" })
        { Assert.Equal(new(CountryLookupStatus.Detected, "BR"), h.Active.Lookup(IPAddress.Parse(address))); }
        Assert.Equal(new(CountryLookupStatus.Detected, "US"), h.Active.Lookup(IPAddress.Parse("203.0.113.1")));
        Assert.Equal(CountryLookupStatus.Unknown, h.Active.Lookup(IPAddress.Parse("198.51.100.1")).Status);
        Assert.Equal(CountryLookupStatus.Unavailable, new MmdbCountryLookup(h.Active).Lookup(IPAddress.Parse("192.0.2.1")).Status);
        Assert.DoesNotContain("synthetic-organization-canary", JsonSerializer.Serialize(manifest), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic.example.invalid", JsonSerializer.Serialize(h.Active.Lookup(IPAddress.Parse("192.0.2.1"))), StringComparison.Ordinal);
    }

    [Fact]
    public void CountryCodeAloneIsSufficientWithoutEnrichmentFields()
    {
        using var h = new Harness();
        var bytes = SyntheticMmdb.CreateLite(Now.AddHours(-1), configure: map =>
        {
            foreach (var field in map.Keys.Where(k => k != "country_code").ToArray())
            { map.Remove(field); }
        });
        var manifest = h.Validator.Validate(bytes, Now, Ct);
        h.Save(manifest, bytes);
        Assert.True(h.Active.TryReplace(manifest, Ct));
        Assert.Equal("BR", h.Active.Lookup(IPAddress.Parse("192.0.2.1")).CountryCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("br")]
    [InlineData("BR ")]
    [InlineData("BRA")]
    [InlineData("ZZ")]
    [InlineData("UK")]
    public void MissingMalformedOrNonIsoCountryCodeRejectsTheWholeArtifact(string? code)
    {
        using var h = new Harness();
        var bytes = SyntheticMmdb.CreateLite(Now.AddHours(-1), configure: map =>
        {
            if (code is null)
            { map.Remove("country_code"); }
            else
            { map["country_code"] = code; }
        });
        var error = Assert.Throws<CountryDatabaseValidationException>(() => h.Validator.Validate(bytes, Now, Ct));
        Assert.Equal("Schema", error.Code);
    }

    [Theory]
    [InlineData("country_code")]
    [InlineData("country")]
    [InlineData("continent_code")]
    [InlineData("continent")]
    [InlineData("asn")]
    [InlineData("as_name")]
    [InlineData("as_domain")]
    public void NonTextFieldsAreNotSilentlyAccepted(string field)
    {
        using var h = new Harness();
        var bytes = SyntheticMmdb.CreateLite(Now.AddHours(-1), configure: map => map[field] = (ushort)7);
        Assert.Equal("Schema", Assert.Throws<CountryDatabaseValidationException>(() => h.Validator.Validate(bytes, Now, Ct)).Code);
    }

    [Theory]
    [InlineData("country_name")]
    [InlineData("continent_name")]
    [InlineData("unexpected")]
    public void LegacyOrUnknownFieldsAreNotTreatedAsLite(string field)
    {
        using var h = new Harness();
        var bytes = SyntheticMmdb.CreateLite(Now.AddHours(-1), configure: map => map[field] = "synthetic");
        Assert.Equal("Schema", Assert.Throws<CountryDatabaseValidationException>(() => h.Validator.Validate(bytes, Now, Ct)).Code);
    }

    [Fact]
    public void LegacyRecordsAreRejectedEvenWithAnApprovedLiteDatabaseType()
    {
        using var h = new Harness();
        var bytes = SyntheticMmdb.Create(Now.AddHours(-1), type: SyntheticMmdb.LiteDatabaseType);
        Assert.Equal("Schema", Assert.Throws<CountryDatabaseValidationException>(() => h.Validator.Validate(bytes, Now, Ct)).Code);
    }

    [Fact]
    public void LiteManifestIdentityIsExplicitAndLegacyManifestCannotBeReused()
    {
        var manifest = Manifest();
        Assert.True(manifest.IsAdmissible(Now));
        Assert.False((manifest with { Product = "country.mmdb" }).IsAdmissible(Now));
        Assert.False((manifest with { Schema = "ipinfo-country-v1" }).IsAdmissible(Now));
        Assert.False((manifest with { Source = "https://ipinfo.io/data/free/country.mmdb" }).IsAdmissible(Now));
        Assert.False((manifest with { License = "unknown" }).IsAdmissible(Now));
        Assert.Equal(manifest, JsonSerializer.Deserialize<CountryDatabaseManifest>(JsonSerializer.Serialize(manifest)));
    }

    [Theory]
    [InlineData("Product")]
    [InlineData("Schema")]
    [InlineData("Source")]
    [InlineData("License")]
    public void MissingPersistedIdentityDoesNotInheritCurrentProductDefaults(string field)
    {
        var manifest = Manifest();
        var json = JsonNode.Parse(JsonSerializer.Serialize(manifest))!.AsObject();
        json.Remove(field);
        var parsed = Assert.IsType<CountryDatabaseManifest>(JsonSerializer.Deserialize<CountryDatabaseManifest>(json.ToJsonString()));
        Assert.False(parsed.IsAdmissible(Now));
    }

    private static CountryDatabaseManifest Manifest() => new(new string('a', 64), 100, Now.AddHours(-1), Now, Now,
        SyntheticMmdb.LiteDatabaseType, "ipinfo_lite.mmdb", "ipinfo-lite-country-v1", "CC-BY-SA-4.0", "https://ipinfo.io/data/ipinfo_lite.mmdb");

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "duora-lite-" + Guid.NewGuid().ToString("N"));
        public CountryDatabaseArtifactValidator Validator { get; } = new(new(SyntheticMmdb.LiteDatabaseType, 1024 * 1024, 100), new FakeTimeProvider(Now));
        public ActiveCountryDatabase Active { get; }
        public Harness()
        {
            Directory.CreateDirectory(_root);
            Active = new(_root, Validator, new FakeTimeProvider(Now));
        }
        public void Save(CountryDatabaseManifest manifest, byte[] bytes) => File.WriteAllBytes(Path.Combine(_root, manifest.Sha256 + ".mmdb"), bytes);
        public void Dispose() { Active.Dispose(); Directory.Delete(_root, true); }
    }
}
