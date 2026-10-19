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

    [Theory]
    [InlineData("--Duora:PublicOrigin=", "Duora:PublicOrigin")]
    [InlineData("--Email:PickupDirectory=", "Email:PickupDirectory")]
    [InlineData("--Outbox:SendTimeout=00:05:00", "Outbox")]
    public async Task WorkerDoesNotStartWithInvalidEmailSettings(string invalidSetting, string expectedKey)
    {
        using var host = BuildHost(new CanaryConnectionString().Value, logs: null, invalidSetting);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expectedKey, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--Database:MaxPoolSize=11")]
    [InlineData("--Database:MaxPoolSize=0")]
    [InlineData("--Database:PoolTimeout=00:01:00")]
    public async Task WorkerDoesNotStartWithAPoolOutsideItsShareOfTheConnectionBudget(string setting)
    {
        using var host = BuildHost(new CanaryConnectionString().Value, logs: null, setting);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Database:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkerPoolIsTenConnectionsByDefault()
    {
        using var host = BuildHost(new CanaryConnectionString().Value, logs: null);

        var connection = new Npgsql.NpgsqlConnectionStringBuilder(
            host.Services.GetRequiredService<Npgsql.NpgsqlDataSource>().ConnectionString);

        Assert.Equal(10, connection.MaxPoolSize);
    }

    [Fact]
    public async Task HeartbeatLogsUnreachableDatabaseWithCauseAndKeepsRunning()
    {
        var logs = new CapturingLoggerProvider();
        using var host = BuildHost(new CanaryConnectionString().Value, logs);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var warning = await logs.FirstWarningFrom(typeof(HeartbeatService).FullName ?? throw new InvalidOperationException())
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.NotNull(warning.Exception);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SelfieQueueDatabaseFailureDoesNotStopTheWorker()
    {
        var logs = new CapturingLoggerProvider();
        var connection = new CanaryConnectionString();
        using var host = BuildHost(connection.Value, logs);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var warning = await logs.FirstWarningFrom(typeof(SelfieVerificationProviderService).FullName ?? throw new InvalidOperationException())
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Null(warning.Exception);
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

    [Fact]
    public async Task WorkerDoesNotStartInProductionWithTechnicalOnlyReview()
    {
        // ADR-0025: aprovação só técnica não serve a fotos reais.
        using var host = BuildHost(
            new CanaryConnectionString().Value,
            logs: null,
            "--environment", "Production",
            "--Media:Review=TechnicalOnly",
            "--Media:Storage:Provider=S3",
            "--Media:Storage:S3:ServiceUrl=https://storage.example.com",
            "--Media:Storage:S3:Region=us-east-1",
            "--Media:Storage:S3:Bucket=duora-media",
            "--Media:Storage:S3:AccessKeyId=synthetic-access",
            "--Media:Storage:S3:SecretAccessKey=synthetic-secret");

        // O host junta as falhas de validação num AggregateException.
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Media:Review não é aceito em Production", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerDoesNotStartInProductionWithAFileSystemRootMissingTheAreaMarker()
    {
        // Adendo do ADR-0010: sem o marcador do Ansible em cada área, o diretório não é o volume do host montado.
        var root = Directory.CreateTempSubdirectory("duora-media-unmounted-");
        try
        {
            using var host = BuildHost(
                new CanaryConnectionString().Value,
                logs: null,
                "--environment", "Production",
                "--Media:Storage:Provider=FileSystem",
                $"--Media:Storage:FileSystemRoot={root.FullName}");

            var exception = await Assert.ThrowsAsync<OptionsValidationException>(
                () => host.StartAsync(TestContext.Current.CancellationToken));

            Assert.Contains("Media:Storage:FileSystemRoot em Production", exception.Message, StringComparison.Ordinal);
            Assert.Contains(".duora-media-area", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WorkerDoesNotStartWithALeaseShorterThanTheSanitizer()
    {
        using var host = BuildHost(
            new CanaryConnectionString().Value,
            logs: null,
            "--Media:Processing:LeaseDuration=00:00:30",
            "--Media:Processing:Sanitizer:Timeout=00:00:30");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("LeaseDuration", exception.Message, StringComparison.Ordinal);
    }

    private static IHost BuildHost(string connectionString, CapturingLoggerProvider? logs, params string[] overrides)
    {
        // Linha de comando tem a maior precedência: nada da máquina (env, user-secrets) interfere.
        var builder = WorkerHost.CreateBuilder(
        [
            "--environment", "Testing",
            $"--ConnectionStrings:Duora={connectionString}",
            "--Duora:PublicOrigin=https://duora.test",
            $"--Email:PickupDirectory={Path.Combine(Path.GetTempPath(), "duora-mail-unused")}",
            .. overrides,
        ]);
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        return builder.Build();
    }
}
