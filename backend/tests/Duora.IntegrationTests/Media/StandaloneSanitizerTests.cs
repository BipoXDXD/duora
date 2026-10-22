extern alias worker;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.MediaProtocol;
using worker::Duora.Worker.Media;

namespace Duora.IntegrationTests.Media;

/// <summary>S1 executable compatibility only; process execution is not a container isolation proof.</summary>
public sealed class StandaloneSanitizerTests
{
    [Fact]
    public async Task StandaloneDecoderPreservesTheEntireSyntheticCorpusAndVariantBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var configuration = new DirectoryInfo(Path.GetDirectoryName(typeof(StandaloneSanitizerTests).Assembly.Location)!).Parent!.Name;
        var executable = Path.Combine(RepositoryRoot.Find(), "backend", "src", "Duora.MediaSanitizer", "bin", configuration,
            "net10.0", "Duora.MediaSanitizer.dll");
        Assert.True(File.Exists(executable), "The standalone sanitizer executable must be built before this compatibility test.");
        var baseline = new ImageSharpPhotoSanitizer(new PhotoSanitizerOptions());
        foreach (var item in SanitizerBaselineCorpus.Cases)
        {
            var request = new SanitizerRequest(RandomNumberGenerator.GetBytes(16), item.Format, item.Create(), ProtocolBudget.Candidate);
            if (request.Content.Length > PhotoLimits.MaxBytes)
            {
                await using var output = new MemoryStream();
                await Assert.ThrowsAsync<InvalidDataException>(() => SanitizerProtocol.WriteRequestAsync(output, request, ct));
                Assert.Equal(0, output.Length);
                continue;
            }
            var expected = await baseline.SanitizeAsync(request.Format, request.Content, ct);
            var actual = await RunAsync(executable, request, ct);
            if (expected is SanitizeOutcome.Rejected rejected)
            {
                Assert.Equal(rejected, actual);
                continue;
            }
            var before = Assert.IsType<SanitizeOutcome.Sanitized>(expected);
            var after = Assert.IsType<SanitizeOutcome.Sanitized>(actual);
            Assert.Equal(before.Detected, after.Detected);
            Assert.Equal(before.Width, after.Width);
            Assert.Equal(before.Height, after.Height);
            Assert.Equal(3, after.Variants.Count);
            foreach (var (left, right) in before.Variants.Zip(after.Variants))
            {
                Assert.Equal(left.Kind, right.Kind);
                Assert.Equal(left.Width, right.Width);
                Assert.Equal(left.Height, right.Height);
                Assert.Equal(left.Content, right.Content);
            }
        }
    }

    private static async Task<SanitizeOutcome> RunAsync(string executable, SanitizerRequest request, CancellationToken ct)
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtime.Parent!.Parent!.Parent!.FullName;
        var start = new ProcessStartInfo(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(executable);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(55));
        try
        {
            var write = WriteAsync();
            var read = SanitizerProtocol.ReadResponseAsync(process.StandardOutput.BaseStream, request, timeout.Token);
            var error = SanitizerProtocol.DrainStderrAsync(process.StandardError.BaseStream, request.Budget.StderrBytes, timeout.Token);
            await Task.WhenAll(write, read, error, process.WaitForExitAsync(timeout.Token));
            Assert.Equal(0, process.ExitCode);
            return await read;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }

        async Task WriteAsync()
        {
            await SanitizerProtocol.WriteRequestAsync(process.StandardInput.BaseStream, request, timeout.Token);
            process.StandardInput.Close();
        }
    }
}
