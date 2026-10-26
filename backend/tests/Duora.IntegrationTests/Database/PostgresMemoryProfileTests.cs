using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Duora.IntegrationTests.Database;

public sealed class PostgresMemoryProfileTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActualSharedMemoryProfileMatchesTheExplicitTestBudgetWithoutChangingConnectionConcurrency()
    {
        var fields = (await DockerFieldAsync("inspect", "{{.HostConfig.ShmSize}} {{.HostConfig.Memory}} {{.HostConfig.MemorySwap}} {{.State.OOMKilled}}", postgres.ContainerId)).Split(' ');
        Assert.Equal(4, fields.Length);
        var sharedMemory = long.Parse(fields[0], CultureInfo.InvariantCulture);
        Assert.Equal(256L * 1024 * 1024, sharedMemory);
        Assert.Equal(PostgresFixture.SharedMemoryBytes, sharedMemory);
        Assert.Equal("0", fields[1]);
        Assert.Equal("0", fields[2]);
        Assert.Equal("false", fields[3]);
        output.WriteLine("HostConfig.ShmSize={0}; HostConfig.Memory={1}; HostConfig.MemorySwap={2}; State.OOMKilled={3}",
            sharedMemory, fields[1], fields[2], fields[3]);

        await using var connection = new NpgsqlConnection(postgres.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select current_setting('max_connections')::int, current_setting('dynamic_shared_memory_type')", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(400, reader.GetInt32(0));
        Assert.Equal("posix", reader.GetString(1));
    }

    [Fact]
    public async Task TestSharedMemoryCapacityFitsWithinTheExistingDockerHostMemory()
    {
        var memory = long.Parse(await DockerFieldAsync("info", "{{.MemTotal}}"), CultureInfo.InvariantCulture);
        Assert.InRange(PostgresFixture.SharedMemoryBytes, 1, memory / 4);
        output.WriteLine("DockerHost.MemTotal={0}; TestPostgres.SharedMemoryBytes={1}; no host or container RAM limit increased",
            memory, PostgresFixture.SharedMemoryBytes);
    }

    private static async Task<string> DockerFieldAsync(string operation, string format, string? containerId = null)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add("--format");
        start.ArgumentList.Add(format);
        if (containerId is not null)
        {
            start.ArgumentList.Add(containerId);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not inspect the disposable Docker test profile.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            return (await stdout).Trim();
        }
        finally
        {
            if (!process.HasExited)
            {
                // Only this read-only CLI child, never the disposable server or another service.
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }
}
