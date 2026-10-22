extern alias sanitizer;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Duora.Core.Media;
using Duora.MediaProtocol;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using sanitizer::Duora.MediaSanitizer.Media;

namespace Duora.IntegrationTests.Media;

/// <summary>S1 executable compatibility only; process execution is not a container isolation proof.</summary>
public sealed class StandaloneSanitizerTests
{
    [Fact]
    public async Task StandaloneDecoderPreservesTheEntireSyntheticCorpusAndVariantBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var executable = ExecutablePath();
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

    [Fact]
    public async Task TransparentPngPreservesAlphaAndMatchesTheExistingEncoder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var image = new Image<Rgba32>(64, 32, new Rgba32(255, 0, 0, 64));
        await using var png = new MemoryStream();
        await image.SaveAsPngAsync(png, ct);
        var request = new SanitizerRequest(RandomNumberGenerator.GetBytes(16), PhotoFormat.Png, png.ToArray(), ProtocolBudget.Candidate);
        var baseline = new ImageSharpPhotoSanitizer(new PhotoSanitizerOptions());
        var expected = Assert.IsType<SanitizeOutcome.Sanitized>(await baseline.SanitizeAsync(request.Format, request.Content, ct));
        var actual = Assert.IsType<SanitizeOutcome.Sanitized>(await RunAsync(ExecutablePath(), request, ct));
        foreach (var (before, after) in expected.Variants.Zip(actual.Variants))
        {
            Assert.Equal(before.Content, after.Content);
            Assert.True(after.Content.AsSpan().IndexOf("ALPH"u8) >= 0);
            using var decoded = Image.Load<Rgba32>(after.Content);
            Assert.Equal(64, decoded[0, 0].A);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ProtocolFailuresExitSilentlyWithoutAResponse(int scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SanitizerRequest(RandomNumberGenerator.GetBytes(16), PhotoFormat.Jpeg, TestImages.Jpeg(),
            scenario == 2 ? ProtocolBudget.Candidate with { VariantBytes = 1 } : ProtocolBudget.Candidate);
        await using var wire = new MemoryStream();
        await SanitizerProtocol.WriteRequestAsync(wire, request, ct);
        var bytes = scenario == 0 ? Array.Empty<byte>() : wire.ToArray();
        if (scenario == 1)
        {
            bytes[26] ^= 1;
        }
        using var process = StartProcess(ExecutablePath());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = process.StandardOutput.BaseStream.ReadAsync(new byte[1], timeout.Token).AsTask();
            var error = process.StandardError.BaseStream.ReadAsync(new byte[1], timeout.Token).AsTask();
            await process.StandardInput.BaseStream.WriteAsync(bytes, timeout.Token);
            process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            Assert.Equal(65, process.ExitCode);
            Assert.Equal(0, await output);
            Assert.Equal(0, await error);
        }
        finally
        {
            await StopIfRunningAsync(process);
        }
    }

    private static string ExecutablePath()
    {
        var configuration = new DirectoryInfo(Path.GetDirectoryName(typeof(StandaloneSanitizerTests).Assembly.Location)!).Parent!.Name;
        var path = Path.Combine(RepositoryRoot.Find(), "backend", "src", "Duora.MediaSanitizer", "bin", configuration,
            "net10.0", "Duora.MediaSanitizer.dll");
        Assert.True(File.Exists(path), "The standalone sanitizer executable must be built before this compatibility test.");
        return path;
    }

    private static Process StartProcess(string executable)
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
        return Process.Start(start)!;
    }

    private static async Task<SanitizeOutcome> RunAsync(string executable, SanitizerRequest request, CancellationToken ct)
    {
        using var process = StartProcess(executable);
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
            await StopIfRunningAsync(process);
        }

        async Task WriteAsync()
        {
            await SanitizerProtocol.WriteRequestAsync(process.StandardInput.BaseStream, request, timeout.Token);
            process.StandardInput.Close();
        }
    }

    private static async Task StopIfRunningAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
        }
    }
}
