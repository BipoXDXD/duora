extern alias worker;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using worker::Duora.Worker;

namespace Duora.IntegrationTests;

public sealed class WorkerHostTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WorkerDoesNotStartWithoutConnectionString(string connectionString)
    {
        using var host = BuildHost(connectionString, logs: null);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ConnectionStrings:Duora", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeartbeatLogsUnreachableDatabaseWithCauseAndKeepsRunning()
    {
        var logs = new CapturingLoggerProvider();
        using var host = BuildHost(new CanaryConnectionString().Value, logs);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var warning = await logs.FirstWarning.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(typeof(HeartbeatService).FullName, warning.Category);
        Assert.NotNull(warning.Exception);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HeartbeatFailureLogsNeverContainThePassword()
    {
        var logs = new CapturingLoggerProvider();
        var connection = new CanaryConnectionString();
        using var host = BuildHost(connection.Value, logs);
        await host.StartAsync(TestContext.Current.CancellationToken);

        await logs.FirstWarning.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains(connection.Password, StringComparison.Ordinal));
    }

    private static IHost BuildHost(string connectionString, CapturingLoggerProvider? logs)
    {
        // Linha de comando tem a maior precedência: nada da máquina (env, user-secrets) interfere.
        var builder = WorkerHost.CreateBuilder(
            ["--environment", "Testing", $"--ConnectionStrings:Duora={connectionString}"]);
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        return builder.Build();
    }
}
