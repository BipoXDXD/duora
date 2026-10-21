using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Duora.IntegrationTests.Identity;

internal sealed class SelfieWorkerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>();
    private readonly List<Signal> _deferred = [];
    private readonly Task _output;
    private readonly Task<string> _error;

    private SelfieWorkerProcess(Process process)
    {
        _process = process;
        _output = ReadOutputAsync();
        _error = process.StandardError.ReadToEndAsync();
    }
    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;

    public static async Task<SelfieWorkerProcess> CreateAsync(string connection, Uri provider, DateTimeOffset time,
        string checkpoint, CancellationToken ct)
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtime.Parent!.Parent!.Parent!.FullName;
        var configuration = new DirectoryInfo(Path.GetDirectoryName(typeof(SelfieWorkerProcess).Assembly.Location)!).Parent!.Name;
        var host = Path.Combine(RepositoryRoot.Find(), "backend", "tests", "Duora.SelfieProcessHost", "bin", configuration, "net10.0", "Duora.SelfieProcessHost.dll");
        Assert.True(File.Exists(host), "Build the integration project and its process-host dependency before running these tests.");
        var start = new ProcessStartInfo(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryRoot.Find(),
        };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add("--synthetic-selfie-worker");
        start.Environment.Clear();
        start.Environment["DOTNET_ROOT"] = dotnetRoot;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start synthetic worker.");
        var worker = new SelfieWorkerProcess(process);
        try
        {
            // Synthetic database connection travels over stdin, never command arguments or logs.
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                ConnectionString = connection,
                ProviderBaseUrl = provider.ToString(),
                Time = time,
                Checkpoint = checkpoint,
            }));
            await process.StandardInput.FlushAsync(ct);
            await worker.WaitAsync("ready", ct);
            return worker;
        }
        catch
        {
            await worker.DisposeAsync();
            throw;
        }
    }

    public async Task<int> CommandAsync(string op, CancellationToken ct, int seconds = 0)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Op = op, Seconds = seconds }));
        await _process.StandardInput.FlushAsync(ct);
        var kind = op switch { "start" => "started", "advance" => "advanced", "probe" => "probed", _ => throw new ArgumentException("Unsupported command.", nameof(op)) };
        return (await WaitAsync(kind, ct)).Count;
    }

    public async Task WaitForCheckpointAsync(CancellationToken ct) => await WaitAsync("checkpoint", ct);

    public async Task ForceKillAsync(CancellationToken ct)
    {
        Assert.False(_process.HasExited, "The worker must still be alive at the chosen interruption point.");
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.NotEqual(0, _process.ExitCode);
        await _output;
    }

    private async Task<Signal> WaitAsync(string kind, CancellationToken ct)
    {
        var cached = _deferred.FindIndex(signal => signal.Kind == kind);
        if (cached >= 0)
        {
            var result = _deferred[cached];
            _deferred.RemoveAt(cached);
            return result;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (await _signals.Reader.WaitToReadAsync(deadline.Token))
        {
            var signal = await _signals.Reader.ReadAsync(deadline.Token);
            Assert.Equal(Id, signal.Pid);
            if (signal.Kind == kind)
            {
                return signal;
            }
            _deferred.Add(signal);
        }
        throw new InvalidOperationException("Synthetic worker exited before the expected signal. " + await _error);
    }

    private async Task ReadOutputAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                var signal = JsonSerializer.Deserialize<Signal>(line) ?? throw new InvalidDataException("Invalid worker signal.");
                await _signals.Writer.WriteAsync(signal);
            }
            _signals.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            _signals.Writer.TryComplete(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await _output;
        await _error;
        _process.Dispose();
    }

    private sealed record Signal(string Kind, int Pid, int Count);
}
