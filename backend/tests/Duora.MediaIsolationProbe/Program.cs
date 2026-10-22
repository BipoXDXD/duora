using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Duora.Core.Media;
using Duora.MediaProtocol;

namespace Duora.MediaIsolationProbe;

/// <summary>Synthetic CI fault injector only. Never included in the sanitizer image.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--idle"])
        {
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        var request = await SanitizerProtocol.ReadRequestAsync(Console.OpenStandardInput(), CancellationToken.None);
        var mode = Encoding.ASCII.GetString(request.Content);
        if (mode is "sleep" or "children")
        {
            if (mode == "children")
            {
                for (var i = 0; i < 16; i++)
                {
                    try
                    {
                        using var child = Process.Start(new ProcessStartInfo("dotnet")
                        {
                            ArgumentList = { "Duora.MediaSanitizer.dll", "--idle" },
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                        });
                    }
                    catch (System.ComponentModel.Win32Exception) { break; }
                }
            }
            await Task.Delay(Timeout.Infinite);
        }
        if (mode == "oom")
        {
            while (true)
            {
                var pointer = Marshal.AllocHGlobal(64 * 1024 * 1024);
                for (var offset = 0; offset < 64 * 1024 * 1024; offset += 4096)
                {
                    Marshal.WriteByte(pointer, offset, 1);
                }
            }
        }
        if (mode is "stdout" or "stderr")
        {
            var stream = mode == "stdout" ? Console.OpenStandardOutput() : Console.OpenStandardError();
            var block = new byte[65536];
            while (true) { await stream.WriteAsync(block); }
        }
        if (mode == "crash")
        {
            byte[] bytes = [.. "RIFF"u8, 24, 0, 0, 0, .. "WEBPVP8 "u8, 12, 0, 0, 0, 16, 0, 0, 0x9d, 1, 0x2a, 40, 0, 20, 0, 0, 0];
            var variants = new[] { PhotoVariant.Thumb, PhotoVariant.Card, PhotoVariant.Full }
                .Select(kind => new SanitizedVariant(kind, bytes, 40, 20)).ToArray();
            await SanitizerProtocol.WriteResponseAsync(Console.OpenStandardOutput(), request,
                new SanitizeOutcome.Sanitized(request.Format, 40, 20, variants), CancellationToken.None);
            return 7;
        }
        if (mode != "inspect") { return 64; }
        var status = await File.ReadAllTextAsync("/proc/self/status");
        var rootReadOnly = false;
        try { await File.WriteAllTextAsync("/app/forbidden", "synthetic"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rootReadOnly = true; }
        var tmpfsBounded = false;
        try
        {
            await using var file = File.Create("/tmp/budget");
            var block = new byte[65536];
            for (var i = 0; i < 300; i++) { await file.WriteAsync(block); }
        }
        catch (IOException) { tmpfsBounded = true; }
        File.Delete("/tmp/budget");
        var noExec = false;
        File.Copy(Environment.ProcessPath!, "/tmp/noexec");
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/tmp/noexec", "--info") { UseShellExecute = false });
            if (process is not null) { await process.WaitForExitAsync(); }
        }
        catch (System.ComponentModel.Win32Exception) { noExec = true; }
        var noNetwork = false;
        using (var client = new TcpClient())
        {
            try { await client.ConnectAsync("1.1.1.1", 443).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) when (error is SocketException or TimeoutException) { noNetwork = true; }
        }
        var noDns = false;
        try { await Dns.GetHostAddressesAsync("example.com").WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception error) when (error is SocketException or TimeoutException) { noDns = true; }
        var result = new
        {
            memory = (await File.ReadAllTextAsync("/sys/fs/cgroup/memory.max")).Trim(),
            swap = (await File.ReadAllTextAsync("/sys/fs/cgroup/memory.swap.max")).Trim(),
            cpu = (await File.ReadAllTextAsync("/sys/fs/cgroup/cpu.max")).Trim(),
            pids = (await File.ReadAllTextAsync("/sys/fs/cgroup/pids.max")).Trim(),
            nonRoot = status.Split('\n').Any(line => line.StartsWith("Uid:", StringComparison.Ordinal) && line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1] != "0"),
            noCapabilities = status.Contains("CapEff:\t0000000000000000", StringComparison.Ordinal),
            noPrivileges = status.Contains("NoNewPrivs:\t1", StringComparison.Ordinal),
            seccomp = status.Contains("Seccomp:\t2", StringComparison.Ordinal),
            noSecrets = !Directory.Exists("/run/secrets") && Environment.GetEnvironmentVariable("CANARY_SECRET") is null,
            noStorage = !Directory.Exists("/media") && !Directory.Exists("/quarantine") && !Directory.Exists("/derivatives"),
            noDockerSocket = !File.Exists("/var/run/docker.sock"),
            rootReadOnly, tmpfsBounded, noExec, noNetwork, noDns,
        };
        await Console.OpenStandardOutput().WriteAsync(JsonSerializer.SerializeToUtf8Bytes(result));
        return 0;
    }
}
