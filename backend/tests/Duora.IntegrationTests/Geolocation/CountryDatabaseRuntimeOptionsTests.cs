using System.Net;
using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.IntegrationTests.Geolocation;

public sealed class CountryDatabaseRuntimeOptionsTests
{
    [Theory]
    [InlineData("DetectorEnabled", false)]
    [InlineData("UpdaterEnabled", true)]
    [InlineData("MonitorEnabled", true)]
    [InlineData("RetentionEnabled", true)]
    public void EnablingAComponentWithoutItsExplicitProfileFailsClosed(string flag, bool worker)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"CountryDatabase:{flag}"] = "true" }).Build();
        var services = new ServiceCollection();
        services.AddOptions<Duora.Infrastructure.DatabaseOptions>();
        services.AddCountryDatabaseRuntime(configuration, worker);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<CountryDatabaseRuntimeOptions>>().Value);
    }

    [Fact]
    public void EnabledReaderIsLocalAndUnavailableUntilACommittedSnapshotIsLoaded()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CountryDatabase:DetectorEnabled"] = "true",
            ["CountryDatabase:Root"] = "/tmp/unused-country-reader",
            ["CountryDatabase:DatabaseType"] = "synthetic-ipinfo-lite",
            ["CountryDatabase:MaxBytes"] = "1048576",
            ["CountryDatabase:MaxNetworks"] = "100",
        }).Build();
        var services = new ServiceCollection();
        services.AddOptions<Duora.Infrastructure.DatabaseOptions>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=synthetic;Password=synthetic;Timeout=1"));
        services.AddCountryDatabaseRuntime(config, worker: false);
        using var provider = services.BuildServiceProvider();
        var lookup = provider.GetRequiredService<ICountryLookup>();
        Assert.True(lookup.Enabled);
        Assert.Equal(CountryLookupStatus.Unavailable, lookup.Lookup(IPAddress.Loopback).Status);
        Assert.False(Directory.Exists("/tmp/unused-country-reader"));
    }

    [Fact]
    public void SecretsAreNotIncludedInOptionsStringRepresentations()
    {
        var o = new CountryDatabaseRuntimeOptions { IpinfoToken = "synthetic-secret-canary" };
        o.Gmail.AppPassword = "synthetic-secret-canary";
        o.Archive.SecretAccessKey = "synthetic-secret-canary";
        Assert.DoesNotContain("synthetic-secret-canary", o.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-canary", o.Gmail.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-canary", o.Archive.ToString(), StringComparison.Ordinal);
    }
}
