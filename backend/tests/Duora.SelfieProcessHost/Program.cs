using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Duora.Core.Identity;
using Duora.Infrastructure;
using Duora.Infrastructure.Identity;
using Duora.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Duora.SelfieProcessHost;

internal static class Program
{
    private static readonly object OutputLock = new();

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--synthetic-selfie-provider"])
        {
            try
            { return await SyntheticProviderHost.RunAsync(); }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.GetType().Name);
                return 1;
            }
        }
        if (args is not ["--synthetic-selfie-worker"])
        {
            return 64;
        }
        try
        {
            var configuration = JsonSerializer.Deserialize<WorkerConfiguration>(await Console.In.ReadLineAsync() ?? "");
            if (configuration is null || !IsLocal(configuration))
            {
                Write("rejected");
                return 64;
            }
            var clock = new ControlledClock(configuration.Time);
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                EnvironmentName = "Testing",
            });
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Duora"] = configuration.ConnectionString,
                ["Duora:PublicOrigin"] = "https://localhost",
                ["Duora:SupportContact"] = "support@example.invalid",
                ["SelfieVerification:Enabled"] = "true",
                ["SelfieVerification:PrivacyApproved"] = "true",
                ["SelfieVerification:PolicyVersion"] = "synthetic-policy",
                ["SelfieVerification:MinimumLiveness"] = "90",
                ["SelfieVerification:MinimumBrightness"] = "40",
                ["SelfieVerification:MinimumSharpness"] = "40",
                ["SelfieVerification:MaximumPoseDegrees"] = "20",
                ["SelfieVerification:CaptureRoleArn"] = "arn:aws:iam::000000000000:role/synthetic",
            });
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddInfrastructure(builder.Configuration, ConnectionShare.Worker);
            builder.Services.AddSelfieVerification();
            // Remove the AWS factory before constructing any service provider.
            builder.Services.RemoveAll<ISelfieProvider>();
            builder.Services.AddSingleton<ISelfieProvider>(new LoopbackProvider(configuration.ProviderBaseUrl));
            using var checkpoint = new CommitCheckpoint(configuration.Checkpoint);
            builder.Services.AddHostedService<SelfieVerificationProviderService>();
            using var host = builder.Build();
            Write("ready");
            while (await Console.In.ReadLineAsync() is { } line)
            {
                var command = JsonSerializer.Deserialize<WorkerCommand>(line) ?? throw new InvalidDataException();
                switch (command.Op)
                {
                    case "start":
                        await host.StartAsync();
                        Write("started");
                        break;
                    case "advance" when command.Seconds is >= 0 and <= 60:
                        clock.Advance(command.Seconds);
                        Write("advanced");
                        break;
                    case "probe":
                        using (var scope = host.Services.CreateScope())
                        {
                            var count = await scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>().DispatchBatchAsync(CancellationToken.None);
                            Write("probed", count);
                        }
                        break;
                    default:
                        throw new InvalidDataException();
                }
            }
            await host.StopAsync();
            return 0;
        }
        catch (Exception exception)
        {
            // Configuration/connection values and payloads never go to stdout or stderr.
            Console.Error.WriteLine(exception.GetType().Name);
            return 1;
        }
    }

    private static bool IsLocal(WorkerConfiguration configuration)
    {
        var connection = new NpgsqlConnectionStringBuilder(configuration.ConnectionString);
        return connection.Host is "127.0.0.1" or "localhost" && connection.Port > 0
            && Uri.TryCreate(configuration.ProviderBaseUrl, UriKind.Absolute, out var provider)
            && provider.Scheme == "http" && provider.Host == "127.0.0.1" && provider.Port > 0
            && configuration.Checkpoint is "none" or "before-session-commit" or "after-session-commit"
                or "before-result-commit" or "after-result-commit";
    }

    internal static void Write(string kind, int count = 0)
    {
        lock (OutputLock)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Kind = kind, Pid = Environment.ProcessId, Count = count }));
        }
    }
}

internal sealed record WorkerConfiguration(string ConnectionString, string ProviderBaseUrl, DateTimeOffset Time, string Checkpoint);
internal sealed record WorkerCommand(string Op, int Seconds);

internal sealed class ControlledClock(DateTimeOffset initial) : TimeProvider
{
    private long _ticks;
    public override DateTimeOffset GetUtcNow() => initial.AddTicks(Interlocked.Read(ref _ticks));
    public void Advance(int seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
}

internal sealed class LoopbackProvider : ISelfieProvider, IDisposable
{
    private readonly HttpClient _client;
    public LoopbackProvider(string address)
    {
        _client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(address) };
    }
    public async Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync($"start/{attemptId:D}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SelfieSession>(cancellationToken) ?? throw new InvalidDataException();
    }
    public async Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync($"read/{reference}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SelfieResult>(cancellationToken) ?? throw new InvalidDataException();
    }
    public void Dispose() => _client.Dispose();
}

// Test-only diagnostic observer: no access to infrastructure internals or production hooks.
internal sealed class CommitCheckpoint : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly string _checkpoint;
    private readonly IDisposable _listeners;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ManualResetEventSlim _hold = new(false);
    private int _paused;

    public CommitCheckpoint(string checkpoint)
    {
        _checkpoint = checkpoint;
        _listeners = DiagnosticListener.AllListeners.Subscribe(this);
    }
    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.EntityFrameworkCore")
        {
            _subscriptions.Add(listener.Subscribe(this));
        }
    }
    public void OnNext(KeyValuePair<string, object?> signal)
    {
        var before = signal.Key == RelationalEventId.TransactionCommitting.Name;
        var after = signal.Key == RelationalEventId.TransactionCommitted.Name;
        if ((!before && !after) || signal.Value is not DbContextEventData { Context: { } context })
        {
            return;
        }
        var entry = context.ChangeTracker.Entries().SingleOrDefault(item => item.Metadata.GetTableName() == "selfie_verifications");
        var state = entry?.Property("State").CurrentValue;
        var matches = (_checkpoint, after, state) switch
        {
            ("before-session-commit", false, SelfieState.Capturing) => true,
            ("after-session-commit", true, SelfieState.Capturing) => true,
            ("before-result-commit", false, SelfieState.Passed) => true,
            ("after-result-commit", true, SelfieState.Passed) => true,
            _ => false,
        };
        if (matches && Interlocked.Exchange(ref _paused, 1) == 0)
        {
            Program.Write("checkpoint");
            _hold.Wait(); // The parent intentionally terminates this OS process at the checkpoint.
        }
    }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose()
    {
        _listeners.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _hold.Set();
        _hold.Dispose();
    }
}
