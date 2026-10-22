using System.Net.Mail;
using Npgsql;

namespace Duora.Infrastructure.Geolocation;

/// <summary>Every operational component is opt-in; measured artifact budgets and secrets are explicit.</summary>
public sealed class CountryDatabaseRuntimeOptions
{
    public const string Section = "CountryDatabase";
    public bool DetectorEnabled { get; set; }
    public bool UpdaterEnabled { get; set; }
    public bool MonitorEnabled { get; set; }
    public bool RetentionEnabled { get; set; }
    public string Root { get; set; } = "";
    public string DatabaseType { get; set; } = "";
    public long MaxBytes { get; set; }
    public int MaxNetworks { get; set; }
    public TimeSpan DownloadTimeout { get; set; }
    public string[] RedirectHosts { get; set; } = [];
    public string IpinfoToken { get; set; } = "";
    public string ApprovedInitialHash { get; set; } = "";
    public string MaintenanceConnectionString { get; set; } = "";
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MonitorInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromMinutes(30);
    public CountryDatabaseGmailOptions Gmail { get; set; } = new();
    public CountryDatabaseArchiveOptions Archive { get; set; } = new();

    internal bool IsConsistent(bool worker)
    {
        var enabled = worker ? UpdaterEnabled || MonitorEnabled || RetentionEnabled : DetectorEnabled;
        if (!enabled)
        { return true; }
        if (!Path.IsPathFullyQualified(Root) || string.IsNullOrWhiteSpace(DatabaseType)
            || MaxBytes is <= 0 or > int.MaxValue || MaxNetworks <= 0
            || RefreshInterval < TimeSpan.FromSeconds(10) || RefreshInterval > TimeSpan.FromMinutes(5))
        { return false; }
        if (!worker)
        { return true; }
        if (UpdaterEnabled && (string.IsNullOrWhiteSpace(IpinfoToken) || DownloadTimeout <= TimeSpan.Zero
            || DownloadTimeout > TimeSpan.FromMinutes(5) || LeaseDuration < TimeSpan.FromMinutes(1)
            || LeaseDuration > TimeSpan.FromMinutes(15) || !ValidHash(ApprovedInitialHash)
            || RedirectHosts.Length == 0 || RedirectHosts.Any(h => h != "dl.assets.ipinfo.io")))
        { return false; }
        if (MonitorEnabled && (!Gmail.IsComplete() || MonitorInterval < TimeSpan.FromMinutes(1) || MonitorInterval > TimeSpan.FromMinutes(15)))
        { return false; }
        if (RetentionEnabled && (!Archive.IsComplete() || RetentionInterval < TimeSpan.FromMinutes(1)
            || RetentionInterval > TimeSpan.FromHours(1) || !IsMaintenanceConnection(MaintenanceConnectionString)))
        { return false; }
        return true;
    }

    internal static bool ValidHash(string hash) => hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsMaintenanceConnection(string value)
    {
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(value);
            return connection.Username == "duora_geo_maintenance" && !string.IsNullOrWhiteSpace(connection.Host)
                && !string.IsNullOrWhiteSpace(connection.Database) && !string.IsNullOrWhiteSpace(connection.Password);
        }
        catch (ArgumentException) { return false; }
    }
}

public sealed class CountryDatabaseGmailOptions
{
    public string Address { get; set; } = "";
    public string AppPassword { get; set; } = "";
    internal bool IsComplete() => MailAddress.TryCreate(Address, out var parsed) && parsed.Address == Address
        && !string.IsNullOrWhiteSpace(AppPassword);
}

public sealed class CountryDatabaseArchiveOptions
{
    public string ServiceUrl { get; set; } = "";
    public string Region { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    internal bool IsComplete() => Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && !string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(Bucket)
        && !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey);
}
