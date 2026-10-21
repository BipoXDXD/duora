using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Duora.IntegrationTests.Identity;

internal sealed partial class SelfieWorkerProcess
{
    public string ContainerId { get; private set; } = "";
    public int ProviderPort { get; private set; }

    public static Task<SelfieWorkerProcess> CreateContainerWorkerAsync(string networkContainer, string connection,
        Uri provider, DateTimeOffset time, string checkpoint, CancellationToken ct) =>
        CreateContainerAsync(networkContainer, false, new
        {
            ConnectionString = connection,
            ProviderBaseUrl = provider.ToString(),
            Time = time,
            Checkpoint = checkpoint,
        }, ct);

    public static Task<SelfieWorkerProcess> CreateContainerProviderAsync(string networkContainer, DateTimeOffset time,
        bool holdFirstStart, CancellationToken ct) =>
        CreateContainerAsync(networkContainer, true, new { Time = time, HoldFirstStart = holdFirstStart }, ct);

    private static async Task<SelfieWorkerProcess> CreateContainerAsync(string networkContainer, bool provider,
        object configuration, CancellationToken ct)
    {
        Assert.Matches("^[a-f0-9]{64}$", networkContainer);
        var root = RepositoryRoot.Find();
        var dockerfile = await File.ReadAllTextAsync(Path.Combine(root, "infra", "images", "dotnet", "Dockerfile"), ct);
        var match = Regex.Match(dockerfile, @"^FROM (mcr\.microsoft\.com/dotnet/runtime:[^\s]+@sha256:[a-f0-9]{64}) AS worker$", RegexOptions.Multiline);
        Assert.True(match.Success, "Use the pinned production worker runtime, without inventing another image.");
        var image = match.Groups[1].Value;
        var inspect = await DockerAsync(ct, "image", "inspect", image, "--format", "{{.Id}}");
        if (inspect.ExitCode != 0)
        {
            Assert.Equal(0, (await DockerAsync(ct, "pull", image)).ExitCode);
        }
        var build = new DirectoryInfo(Path.GetDirectoryName(typeof(SelfieWorkerProcess).Assembly.Location)!).Parent!.Name;
        var binaries = Path.Combine(root, "backend", "tests", "Duora.SelfieProcessHost", "bin", build, "net10.0");
        Assert.True(File.Exists(Path.Combine(binaries, "Duora.SelfieProcessHost.dll")));
        var name = "duora-selfie-test-" + Guid.NewGuid().ToString("N");
        var cid = Path.Combine(Path.GetTempPath(), name + ".cid");
        var start = DockerStart("run", "--interactive", "--name", name, "--cidfile", cid,
            "--label", "duora.synthetic-selfie-test=" + name, "--network", "container:" + networkContainer,
            "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges", "--pids-limit=128",
            "--memory", provider ? "256m" : "512m", "--tmpfs", "/tmp:rw,nosuid,noexec,size=64m",
            "--env", "DOTNET_EnableDiagnostics=0", "--user", "1654:1654", "--workdir", "/app",
            "--mount", "type=bind,source=" + binaries + ",target=/app,readonly", "--entrypoint", "dotnet", image,
            "/app/Duora.SelfieProcessHost.dll", provider ? "--synthetic-selfie-provider" : "--synthetic-selfie-worker");
        start.RedirectStandardInput = true;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start synthetic container.");
        var worker = new SelfieWorkerProcess(process, name, cid);
        try
        {
            // Only disposable configuration uses stdin; no connection values enter Docker arguments or environment.
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(configuration));
            await process.StandardInput.FlushAsync(ct);
            var ready = await worker.WaitAsync("ready", ct);
            worker.ContainerId = (await File.ReadAllTextAsync(cid, ct)).Trim();
            Assert.Matches("^[a-f0-9]{64}$", worker.ContainerId);
            worker.ProviderPort = ready.Port;
            return worker;
        }
        catch
        {
            await worker.DisposeAsync();
            throw;
        }
    }

    public async Task<(bool Running, int HostPid, int ExitCode)> InspectContainerAsync(CancellationToken ct)
    {
        var state = await DockerAsync(ct, "inspect", "--format", "{{.State.Running}} {{.State.Pid}} {{.State.ExitCode}}", ContainerId);
        Assert.Equal(0, state.ExitCode);
        var values = state.Output.Trim().Split(' ');
        Assert.Equal(3, values.Length);
        return (bool.Parse(values[0]), int.Parse(values[1], CultureInfo.InvariantCulture), int.Parse(values[2], CultureInfo.InvariantCulture));
    }

    public async Task<(int Starts, int Reads, int Sessions, bool FirstResponseWritten)> ProviderStatsAsync(CancellationToken ct)
    {
        await ProviderCommandAsync("stats", ct);
        var signal = await WaitAsync("stats", ct);
        return (signal.Starts, signal.Reads, signal.Sessions, signal.FirstResponseWritten);
    }

    public async Task WaitForProviderStartAsync(CancellationToken ct)
    {
        await ProviderCommandAsync("wait-start", ct);
        await WaitAsync("accepted", ct);
    }

    public async Task ReleaseProviderResponseAsync(CancellationToken ct)
    {
        await ProviderCommandAsync("release", ct);
        await WaitAsync("released", ct);
    }

    private async Task ProviderCommandAsync(string op, CancellationToken ct)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Op = op, Seconds = 0 }));
        await _process.StandardInput.FlushAsync(ct);
    }

    private async Task KillContainerAsync(CancellationToken ct)
    {
        var label = await DockerAsync(ct, "inspect", "--format", "{{index .Config.Labels \"duora.synthetic-selfie-test\"}}", ContainerId);
        Assert.Equal(0, label.ExitCode);
        Assert.Equal(_containerName, label.Output.Trim());
        Assert.True((await InspectContainerAsync(ct)).Running);
        Assert.Equal(0, (await DockerAsync(ct, "kill", "--signal", "KILL", ContainerId)).ExitCode);
        var stopped = await InspectContainerAsync(ct);
        Assert.False(stopped.Running);
        Assert.Equal(137, stopped.ExitCode);
    }

    private async Task RemoveContainerAsync()
    {
        // The name is generated here, never supplied by a caller or discovered from another workload.
        var removed = await DockerAsync(CancellationToken.None, "rm", "--force", _containerName!);
        Assert.True(removed.ExitCode == 0 || removed.Error.Contains("No such container", StringComparison.Ordinal),
            "Unable to remove this test's synthetic container.");
    }

    private static ProcessStartInfo DockerStart(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryRoot.Find(),
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    private static async Task<(int ExitCode, string Output, string Error)> DockerAsync(CancellationToken ct, params string[] arguments)
    {
        using var process = Process.Start(DockerStart(arguments)) ?? throw new InvalidOperationException("Unable to execute test Docker command.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(2), ct);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
