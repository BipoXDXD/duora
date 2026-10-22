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
        var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        Assert.NotNull(line);
        using var ready = JsonDocument.Parse(line);
        Options = new IsolatedPhotoSanitizerOptions
        {
            LauncherSocket = Path.Combine(_root.FullName, "control.sock"),
            LauncherUid = ready.RootElement.GetProperty("uid").GetInt32(),
            ProfileSha256 = ready.RootElement.GetProperty("digest").GetString()!,
        };
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
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(cleanup.Token);
            }
            _process.Dispose();
        }
        _root.Delete(recursive: true);
    }
}
