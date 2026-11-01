extern alias worker;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using worker::Duora.Worker.Media;

namespace Duora.IntegrationTests.Media;

/// <summary>Real Unix/S2 framing with a standalone process; not a native sandbox proof.</summary>
internal sealed class LauncherFixture : IAsyncDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dml-");
    private Process? _process;
    private Task _stderrDrain = Task.CompletedTask;
    private long _stderrCharacters;

    public IsolatedPhotoSanitizerOptions Options { get; private set; } = new();

    public bool Started => File.Exists(Path.Combine(_root.FullName, "started"));

    public int AdmissionCount
    {
        get
        {
            using var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root.FullName, "journal.json")));
            return journal.RootElement.GetArrayLength();
        }
    }

    public string[] Arguments =>
    [
        $"--Media:Processing:Sanitizer:LauncherSocket={Options.LauncherSocket}",
        $"--Media:Processing:Sanitizer:LauncherUid={Options.LauncherUid}",
        $"--Media:Processing:Sanitizer:ProfileSha256={Options.ProfileSha256}",
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The isolated Worker tests require the Linux CI runner.");
        }
        File.SetUnixFileMode(_root.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SetMode("normal");
        var configuration = new DirectoryInfo(Path.GetDirectoryName(typeof(LauncherFixture).Assembly.Location)!).Parent!.Name;
        var repository = RepositoryRoot.Find();
        var decoder = Path.Combine(repository, "backend", "src", "Duora.MediaSanitizer", "bin", configuration,
            "net10.0", "Duora.MediaSanitizer.dll");
        Assert.True(File.Exists(decoder));
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
        var start = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(repository, "backend", "tests", "Duora.IntegrationTests", "Media", "launcher_fixture.py"));
        start.ArgumentList.Add(_root.FullName);
        start.ArgumentList.Add(Path.Combine(runtime, "dotnet"));
        start.ArgumentList.Add(decoder);
        _process = Process.Start(start)!;
        var stderr = _process.StandardError;
        _stderrDrain = Task.Run(() => DrainStandardErrorAsync(stderr), CancellationToken.None);
        string? line;
        try
        {
            line = await _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Launcher fixture readiness timed out ({StartupState()}).", error);
        }
        if (line is null)
        {
            throw new InvalidOperationException($"Launcher fixture stdout ended before readiness ({StartupState()}).");
        }
        using var ready = JsonDocument.Parse(line);
        Options = new IsolatedPhotoSanitizerOptions
        {
            LauncherSocket = Path.Combine(_root.FullName, "control.sock"),
            LauncherUid = ready.RootElement.GetProperty("uid").GetInt32(),
            ProfileSha256 = ready.RootElement.GetProperty("digest").GetString()!,
        };
    }

    // Consume until EOF with a fixed buffer; stopping at a size cap can block the child again.
    private async Task DrainStandardErrorAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            Interlocked.Add(ref _stderrCharacters, count);
        }
    }

    private string StartupState()
    {
        var state = _process!.HasExited ? $"exited:{_process.ExitCode}" : "running";
        return $"state={state}; stderrCharacters={Interlocked.Read(ref _stderrCharacters)}; "
            + $"socketPresent={File.Exists(Path.Combine(_root.FullName, "control.sock"))}";
    }

    public void SetMode(string mode) => File.WriteAllText(Path.Combine(_root.FullName, "mode"), mode);

    public async Task WaitForAsync(string marker, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!File.Exists(Path.Combine(_root.FullName, marker)))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(cleanup.Token);
                }
                await _stderrDrain.WaitAsync(cleanup.Token);
            }
            finally
            {
                _process.Dispose();
            }
        }
        _root.Delete(recursive: true);
    }
}
