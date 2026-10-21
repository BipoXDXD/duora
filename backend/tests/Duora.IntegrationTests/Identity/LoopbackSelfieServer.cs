using System.Collections.Concurrent;
using Duora.Core.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Duora.IntegrationTests.Identity;

internal sealed class LoopbackSelfieServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SelfieSession> _sessions = new();
    private readonly TaskCompletionSource _acceptedStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WebApplication _app;
    private CancellationToken _firstStartAborted;
    private int _starts;
    private int _reads;

    private LoopbackSelfieServer(WebApplication app) => _app = app;
    public Uri Address { get; private set; } = null!;
    public int Starts => Volatile.Read(ref _starts);
    public int Reads => Volatile.Read(ref _reads);
    public int Sessions => _sessions.Count;
    public bool FirstStartAborted => _firstStartAborted.IsCancellationRequested;

    public static async Task<LoopbackSelfieServer> StartAsync(DateTimeOffset time, bool loseFirstResponse, CancellationToken ct)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new LoopbackSelfieServer(app);
        app.MapPost("/start/{attempt:guid}", async (Guid attempt, HttpContext context) =>
        {
            var session = server._sessions.GetOrAdd(attempt, _ => new(Guid.NewGuid().ToString(), time.AddMinutes(3)));
            var call = Interlocked.Increment(ref server._starts);
            if (loseFirstResponse && call == 1)
            {
                server._firstStartAborted = context.RequestAborted;
                server._acceptedStart.TrySetResult();
                await server._releaseStart.Task.WaitAsync(context.RequestAborted);
            }
            return Results.Json(session);
        });
        app.MapPost("/read/{reference:guid}", (Guid reference) =>
        {
            if (!server._sessions.Values.Any(session => session.Reference == reference.ToString()))
            {
                return Results.NotFound();
            }
            Interlocked.Increment(ref server._reads);
            return Results.Json(new SelfieResult(SelfieState.Passed, null, "synthetic-policy"));
        });
        await app.StartAsync(ct);
        server.Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/");
        return server;
    }

    public Task WaitForAcceptedStartAsync(CancellationToken ct) => _acceptedStart.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    public void ReleaseStartResponse() => _releaseStart.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        _releaseStart.TrySetResult();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
