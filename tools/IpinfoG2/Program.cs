using System.Runtime.InteropServices;
using System.Text.Json;
using Duora.Infrastructure.Geolocation;

namespace Duora.Tools.IpinfoG2;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--self-test"])
        { return await SelfTest.RunAsync(); }
        var temporaryRoot = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (args is not ["--rehearse"] || string.IsNullOrEmpty(temporaryRoot)
            || Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "BipoXDXD/duora"
            || Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/main"
            || Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") != "workflow_dispatch")
        {
            Console.Error.WriteLine("G2 blocked: manual main rehearsal required.");
            return 1;
        }
        var reportPath = Path.Combine(temporaryRoot, "ipinfo-g2-report.json");
        try
        {
            var mode = Environment.GetEnvironmentVariable("G2_MODE") ?? "";
            var profileJson = Environment.GetEnvironmentVariable("G2_PROFILE_JSON") ?? "";
            if (profileJson.Length > 32768)
            { throw new G2Failure("Configuration"); }
            var profile = G2Profile.Parse(profileJson, mode);
            var token = Environment.GetEnvironmentVariable("G2_IPINFO_LITE_DOWNLOAD_TOKEN") ?? "";
            Environment.SetEnvironmentVariable("G2_IPINFO_LITE_DOWNLOAD_TOKEN", null);
            var evidence = await G2Rehearsal.RunAsync(profile, mode, token, temporaryRoot);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                Status = "measured", GateAccepted = false, profile.Authorization,
                Commit = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                RunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                Attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                Product = IpinfoLiteSchema.Product, Schema = IpinfoLiteSchema.Schema,
                Source = IpinfoLiteSchema.Source, License = IpinfoLiteSchema.License, Attribution = "IPinfo https://ipinfo.io",
                Runtime = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Clock = "Runner UTC; not independently verified", Evidence = evidence,
            }));
            Console.WriteLine("G2 sanitized evidence recorded. G2 acceptance and operational activation remain pending.");
            return 0;
        }
        catch (Exception error)
        {
            var code = error switch
            {
                G2Failure failure => failure.Code,
                CountryDatabaseUpdateException failure => failure.Code,
                CountryDatabaseValidationException failure => failure.Code,
                OperationCanceledException => "Timeout",
                _ => "RehearsalFailure",
            };
            // Deliberately omit exception messages/stacks, remote bodies, headers, paths and signed URLs.
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { Status = "failed", Code = code, GateAccepted = false }));
            Console.Error.WriteLine("G2 failed: " + code + ". No operational approval or activation.");
            return 1;
        }
    }
}
