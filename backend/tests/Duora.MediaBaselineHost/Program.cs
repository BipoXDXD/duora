using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Duora.Core.Media;
using Duora.Worker.Media;
using SixLabors.ImageSharp;

namespace Duora.MediaBaselineHost;

// Synthetic test harness only. No host startup, storage adapter, network, credentials or production registration.
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || !Enum.TryParse<PhotoFormat>(args[0], out var declared)
            || !Enum.IsDefined(declared) || new FileInfo(args[1]).Length > PhotoLimits.MaxBytes + 1)
        {
            return 64;
        }
        var original = await File.ReadAllBytesAsync(args[1]);
        var sanitizer = new ImageSharpPhotoSanitizer(new PhotoSanitizerOptions());
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var beforeRss = process.WorkingSet64;
        var beforeCgroup = ReadCgroupCurrent();
        var beforeCpu = process.TotalProcessorTime;
        var beforeAllocated = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        var outcome = await sanitizer.SanitizeAsync(declared, original, CancellationToken.None);
        timer.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - beforeAllocated;
        process.Refresh();
        var peakRss = process.PeakWorkingSet64;
        var cpuMilliseconds = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds;
        var afterCgroup = ReadCgroupCurrent();
        // Validation/hashing below is outside the recorded sanitization interval and peak sample.
        var variants = outcome is SanitizeOutcome.Sanitized sanitized
            ? sanitized.Variants.Select(variant =>
            {
                var info = Image.Identify(variant.Content);
                return new
                {
                    Kind = PhotoVariants.Name(variant.Kind),
                    Bytes = variant.Content.Length,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(variant.Content)),
                    variant.Width,
                    variant.Height,
                    IdentifiedWidth = info.Width,
                    IdentifiedHeight = info.Height,
                    Format = PhotoFormats.Sniff(variant.Content)?.ToString(),
                    HasMetadata = info.Metadata.ExifProfile is not null || info.Metadata.XmpProfile is not null
                        || info.Metadata.IptcProfile is not null || info.Metadata.IccProfile is not null
                        || info.Metadata.CicpProfile is not null,
                };
            }).ToArray()
            : [];
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            ProcessId = Environment.ProcessId,
            InputBytes = original.Length,
            InputSha256 = Convert.ToHexStringLower(SHA256.HashData(original)),
            DeclaredFormat = declared.ToString(),
            Outcome = outcome is SanitizeOutcome.Sanitized ? "Sanitized" : "Rejected",
            RejectionCode = outcome is SanitizeOutcome.Rejected rejected ? PhotoRejections.Code(rejected.Reason) : null,
            Width = (outcome as SanitizeOutcome.Sanitized)?.Width,
            Height = (outcome as SanitizeOutcome.Sanitized)?.Height,
            Variants = variants,
            SanitizerElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            SanitizerCpuMilliseconds = cpuMilliseconds,
            AllocatedDuringSanitizeBytes = allocated,
            BaselineWorkingSetBytes = beforeRss,
            PeakWorkingSetBytes = peakRss,
            CgroupMemoryCurrentBeforeBytes = beforeCgroup,
            CgroupMemoryCurrentAfterBytes = afterCgroup,
            CgroupScope = "inherited-shared-not-attributable-to-child",
            Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Os = RuntimeInformation.OSDescription,
            ServerGc = GCSettings.IsServerGC,
        }));
        return 0;
    }

    private static long? ReadCgroupCurrent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        var line = File.ReadLines("/proc/self/cgroup").FirstOrDefault(value => value.StartsWith("0::", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }
        var file = Path.Combine("/sys/fs/cgroup", line[3..].TrimStart('/'), "memory.current");
        return File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var current) ? current : null;
    }
}
