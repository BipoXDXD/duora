extern alias worker;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using worker::Duora.Worker;

namespace Duora.IntegrationTests.Geolocation;

public sealed class GeolocationHostRegistrationTests
{
    [Theory]
    [InlineData("UpdaterEnabled", "CountryDatabaseUpdateWorker")]
    [InlineData("MonitorEnabled", "CountryDatabaseHealthWorker")]
    [InlineData("RetentionEnabled", "CountryDatabaseRetentionWorker")]
    public void ExplicitlyEnabledComponentIsRegistered(string flag, string service)
    {
        using var host = Build($"--CountryDatabase:{flag}=true");
        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s.GetType().Name == service);
    }

    [Fact]
    public void DefaultsDoNotRegisterAnyCountryDatabaseWorker()
    {
        using var host = Build();
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), s => s.GetType().Name.StartsWith("CountryDatabase", StringComparison.Ordinal));
    }

    private static IHost Build(params string[] args) => WorkerHost.CreateBuilder([
        "--environment=Testing", "--ConnectionStrings:Duora=Host=127.0.0.1;Port=1;Database=duora;Username=duora_app;Password=synthetic;Timeout=1",
        "--Duora:PublicOrigin=https://duora.test", "--Email:PickupDirectory=/tmp/unused-duora-mail",
        "--Database:MaxPoolSize=8", "--CountryDatabase:Root=/tmp/duora-synthetic-country",
        "--CountryDatabase:DatabaseType=synthetic-ipinfo-lite", "--CountryDatabase:MaxBytes=67108864",
        "--CountryDatabase:MaxNetworks=10000000", "--CountryDatabase:DownloadTimeout=00:03:00",
        "--CountryDatabase:IpinfoToken=synthetic-token", $"--CountryDatabase:ApprovedInitialHash={new string('a', 64)}",
        "--CountryDatabase:RedirectHosts:0=dl.assets.ipinfo.io",
        "--CountryDatabase:MaintenanceConnectionString=Host=127.0.0.1;Port=1;Database=duora;Username=duora_geo_maintenance;Password=synthetic;Timeout=1",
        "--CountryDatabase:Gmail:Address=operator@example.invalid", "--CountryDatabase:Gmail:AppPassword=synthetic-password",
        "--CountryDatabase:Archive:ServiceUrl=https://s3.us-east-005.backblazeb2.com",
        "--CountryDatabase:Archive:Region=us-east-005", "--CountryDatabase:Archive:Bucket=synthetic-ipinfo-private",
        "--CountryDatabase:Archive:AccessKeyId=synthetic-key", "--CountryDatabase:Archive:SecretAccessKey=synthetic-secret",
        .. args,
    ]).Build();
}
