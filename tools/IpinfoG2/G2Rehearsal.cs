using Duora.Infrastructure.Geolocation;

namespace Duora.Tools.IpinfoG2;

internal static class G2Rehearsal
{
    public static async Task<G2Evidence> RunAsync(G2Profile profile, string mode, string token, string temporaryRoot,
        HttpMessageHandler? syntheticHandler = null)
    {
        profile.Validate(mode);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsControl))
        { throw new G2Failure("CredentialMissingOrInvalid"); }
        var directory = Path.Combine(temporaryRoot, "ipinfo-g2", Guid.NewGuid().ToString("N"));
        if (!OperatingSystem.IsLinux())
        { throw new G2Failure("UnsupportedRehearsalPlatform"); }
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(profile.TimeoutSeconds));
            var options = new CountryDatabaseDownloadOptions(profile.MaxBytes, TimeSpan.FromSeconds(profile.TimeoutSeconds),
                new HashSet<string>(profile.RedirectHosts, StringComparer.Ordinal));
            using var downloader = syntheticHandler is null
                ? CountryDatabaseDownloader.Create(options, token, TimeProvider.System)
                : new CountryDatabaseDownloader(syntheticHandler, options, token, TimeProvider.System);
            var path = Path.Combine(directory, "ipinfo_lite.mmdb");
            var downloadedAt = await downloader.DownloadAsync(path, timeout.Token);
            await using var file = File.OpenRead(path);
            if (file.Length is <= 0 or > int.MaxValue || file.Length > profile.MaxBytes)
            { throw new G2Failure("BytesLimit"); }
            var bytes = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(bytes, timeout.Token);
            if (file.ReadByte() != -1)
            { throw new G2Failure("BytesLimit"); }
            return G2Evidence.Measure(bytes, downloadedAt, profile, mode, timeout.Token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
