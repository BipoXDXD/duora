using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Duora.Core.Media;
using worker::Duora.Worker.Media;

namespace Duora.IntegrationTests.Media;

/// <summary>Characterizes the current implementation in fresh processes; no new limit or sandbox is enabled.</summary>
[Trait("Acceptance", "T-16")]
public sealed class SanitizerResourceEnvelopeTests
{
    [Fact]
    public void SyntheticZipHasAFixedTimestampForReproducibleInputHashes()
    {
        var input = TestImages.JpegZipPolyglot();
        var offset = input.AsSpan().IndexOf("PK\u0003\u0004"u8);
        Assert.True(offset >= 0);
        using var stream = new MemoryStream(input[offset..]);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(new DateTime(2000, 1, 1), Assert.Single(zip.Entries).LastWriteTime.DateTime);
        Assert.Equal(input, TestImages.JpegZipPolyglot());
    }

    [Fact]
    public async Task CurrentSanitizerHasRepeatableSyntheticResultsAndIsolatedProcessMeasurements()
    {
        var ct = TestContext.Current.CancellationToken;
        var native = NativeCorpusOptions.Read(Environment.GetEnvironmentVariable);
        using var isolated = native is null ? null : new IsolatedPhotoSanitizer(new IsolatedPhotoSanitizerOptions
        {
            LauncherSocket = native.Socket,
            LauncherUid = native.LauncherUid,
            ProfileSha256 = native.ProfileSha256,
        });
        if (isolated is not null)
        {
            await isolated.EnsureReadyAsync(ct);
        }
        var nativeSamples = new List<object>();
        var folder = Path.Combine(Path.GetTempPath(), $"duora-media-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var summary = new StringBuilder("\n## Current sanitizer baseline (synthetic, not sandbox homologation)\n\n"
            + "Each row uses two fresh processes; elapsed time is whole SanitizeAsync, not decode-only. "
            + "RSS includes child runtime/input; cgroup values are shared runner context, not attributed to the child.\n\n"
            + "| Case | Input bytes | Result | Max elapsed ms | Max RSS bytes | Total output bytes |\n"
            + "|---|---:|---|---:|---:|---:|\n");
        var raw = new StringBuilder("\n<details><summary>Raw synthetic samples (JSONL)</summary>\n\n```jsonl\n");
        var pids = new HashSet<int>();
        try
        {
            foreach (var item in SanitizerBaselineCorpus.Cases)
            {
                var input = item.Create();
                Assert.InRange(input.Length, 1, PhotoLimits.MaxBytes + (item.Name == "jpeg-byte-limit" ? 1 : 0));
                var file = Path.Combine(folder, item.Name + ".bin");
                await File.WriteAllBytesAsync(file, input, ct);
                var first = await MeasureAsync(item.Format, file, ct);
                var second = await MeasureAsync(item.Format, file, ct);
                Assert.Equal(first.GetProperty("Variants").GetRawText(), second.GetProperty("Variants").GetRawText());
                foreach (var result in new[] { first, second })
                {
                    Assert.Equal(1, result.GetProperty("SchemaVersion").GetInt32());
                    var pid = result.GetProperty("ProcessId").GetInt32();
                    Assert.NotEqual(Environment.ProcessId, pid);
                    Assert.True(pids.Add(pid), "Every sample must come from a new OS process.");
                    Assert.Equal(input.Length, result.GetProperty("InputBytes").GetInt32());
                    Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(input)), result.GetProperty("InputSha256").GetString());
                    Assert.True(result.GetProperty("PeakWorkingSetBytes").GetInt64() > 0);
                    Assert.True(result.GetProperty("AllocatedDuringSanitizeBytes").GetInt64() >= 0);
                    Assert.Equal(item.RejectionCode, result.GetProperty("RejectionCode").GetString());
                    Assert.Equal(item.RejectionCode is null ? "Sanitized" : "Rejected", result.GetProperty("Outcome").GetString());
                    ValidateVariants(item, result);
                    var record = JsonSerializer.Serialize(new { Case = item.Name, Seed = SanitizerBaselineCorpus.Seed, Sample = result });
                    raw.AppendLine(record);
                    TestContext.Current.TestOutputHelper!.WriteLine($"sanitizer-baseline {record}");
                }
                var total = first.GetProperty("Variants").EnumerateArray().Sum(v => v.GetProperty("Bytes").GetInt64());
                var elapsed = Math.Max(first.GetProperty("SanitizerElapsedMilliseconds").GetDouble(), second.GetProperty("SanitizerElapsedMilliseconds").GetDouble());
                var peak = Math.Max(first.GetProperty("PeakWorkingSetBytes").GetInt64(), second.GetProperty("PeakWorkingSetBytes").GetInt64());
                if (isolated is not null)
                {
                    var timer = Stopwatch.StartNew();
                    var actual = await isolated.SanitizeAsync(item.Format, input, ct);
                    timer.Stop();
                    NativeSanitizerCorpus.AssertEquivalent(item, first, actual);
                    nativeSamples.Add(new
                    {
                        name = item.Name,
                        inputBytes = input.Length,
                        inputSha256 = Convert.ToHexStringLower(SHA256.HashData(input)),
                        milliseconds = timer.Elapsed.TotalMilliseconds,
                        baseline = first,
                        outcome = actual is SanitizeOutcome.Sanitized ? "Sanitized" : "Rejected",
                        rejectionCode = (actual as SanitizeOutcome.Rejected) is { } rejected ? PhotoRejections.Code(rejected.Reason) : null,
                        width = (actual as SanitizeOutcome.Sanitized)?.Width,
                        height = (actual as SanitizeOutcome.Sanitized)?.Height,
                        variants = (actual as SanitizeOutcome.Sanitized)?.Variants.Select(v => new
                        {
                            kind = PhotoVariants.Name(v.Kind),
                            bytes = v.Content.Length,
                            sha256 = Convert.ToHexStringLower(SHA256.HashData(v.Content)),
                            width = v.Width,
                            height = v.Height,
                        }).ToArray(),
                    });
                }
                summary.AppendLine(CultureInfo.InvariantCulture,
                    $"| {item.Name} | {input.Length} | {item.RejectionCode ?? "Sanitized"} | {elapsed:F3} | {peak} | {total} |");
                File.Delete(file);
            }
            if (native is not null)
            {
                Assert.Equal(18, SanitizerBaselineCorpus.Cases.Count);
                Assert.Equal(18, nativeSamples.Count);
                await using var report = new FileStream(native.Report, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(report, new
                {
                    schema = 1,
                    profileSha256 = native.ProfileSha256,
                    complete = true,
                    cases = nativeSamples,
                }, cancellationToken: ct);
            }
            // Measurements are observations. Candidate 512 MiB/50s/16+24 MiB caps are not pass thresholds here.
            summary.AppendLine("\nNo enforcement, startup/launcher benchmark, worst-case proof or production capacity approval. "
                + "Input/output hashes, per-variant sizes and raw measurements are retained in the test output.\n");
            var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                Assert.False(string.IsNullOrWhiteSpace(summaryPath), "GitHub Actions must retain all raw samples in the job summary.");
            }
            if (summaryPath is { Length: > 0 } target)
            {
                raw.AppendLine("```\n</details>\n");
                await File.AppendAllTextAsync(target, summary.ToString() + raw, ct);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void ValidateVariants(SanitizerBaselineCase item, JsonElement result)
    {
        var variants = result.GetProperty("Variants").EnumerateArray().ToArray();
        if (item.RejectionCode is not null)
        {
            Assert.Empty(variants);
            return;
        }
        Assert.Equal(item.Width, result.GetProperty("Width").GetInt32());
        Assert.Equal(item.Height, result.GetProperty("Height").GetInt32());
        Assert.Equal(PhotoVariants.All.Select(PhotoVariants.Name), variants.Select(v => v.GetProperty("Kind").GetString()));
        foreach (var (variant, kind) in variants.Zip(PhotoVariants.All))
        {
            var (width, height) = PhotoVariants.Fit(item.Width, item.Height, PhotoVariants.MaxSide(kind));
            Assert.Equal(width, variant.GetProperty("Width").GetInt32());
            Assert.Equal(height, variant.GetProperty("Height").GetInt32());
            Assert.Equal(width, variant.GetProperty("IdentifiedWidth").GetInt32());
            Assert.Equal(height, variant.GetProperty("IdentifiedHeight").GetInt32());
            Assert.Equal("WebP", variant.GetProperty("Format").GetString());
            Assert.False(variant.GetProperty("HasMetadata").GetBoolean());
            Assert.True(variant.GetProperty("Bytes").GetInt32() > 0);
        }
    }

    private static async Task<JsonElement> MeasureAsync(PhotoFormat format, string input, CancellationToken ct)
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtime.Parent!.Parent!.Parent!.FullName;
        var configuration = new DirectoryInfo(Path.GetDirectoryName(typeof(SanitizerResourceEnvelopeTests).Assembly.Location)!).Parent!.Name;
        var host = Path.Combine(RepositoryRoot.Find(), "backend", "tests", "Duora.MediaBaselineHost", "bin", configuration,
            "net10.0", "Duora.MediaBaselineHost.dll");
        Assert.True(File.Exists(host), "Build the integration project and its test-only baseline host before running.");
        var start = new ProcessStartInfo(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add(format.ToString());
        start.ArgumentList.Add(input);
        start.Environment.Clear();
        start.Environment["DOTNET_ROOT"] = dotnetRoot;
        start.Environment["DOTNET_NOLOGO"] = "true";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start synthetic baseline host.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error);
            using var document = JsonDocument.Parse(await output);
            return document.RootElement.Clone();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
extern alias worker;

