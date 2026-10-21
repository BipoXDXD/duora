using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Duora.Core.Identity;

namespace Duora.SelfieProcessHost;

// Only the container regression harness starts this HTTP fake. No SDK, image or credential is used.
internal static class SyntheticProviderHost
{
    public static async Task<int> RunAsync()
    {
        var configuration = JsonSerializer.Deserialize<Configuration>(await Console.In.ReadLineAsync() ?? "")
            ?? throw new InvalidDataException();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stop = new CancellationTokenSource();
        var sessions = new ConcurrentDictionary<Guid, SelfieSession>();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var reads = 0;
        var firstResponseWritten = 0;
        var serving = ServeAsync();
        Write("ready");
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                var command = JsonSerializer.Deserialize<WorkerCommand>(line) ?? throw new InvalidDataException();
                switch (command.Op)
                {
                    case "wait-start":
                        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(30));
                        Write("accepted");
                        break;
                    case "release":
                        release.TrySetResult();
                        Write("released");
                        break;
                    case "stats":
                        Write("stats");
                        break;
                    default:
                        throw new InvalidDataException();
                }
            }
        }
        finally
        {
            release.TrySetResult();
            await stop.CancelAsync();
            listener.Stop();
            await serving;
        }
        return 0;

        void Write(string kind) => Console.WriteLine(JsonSerializer.Serialize(new
        {
            Kind = kind,
            Pid = Environment.ProcessId,
            Count = 0,
            Port = port,
            Starts = Volatile.Read(ref starts),
            Reads = Volatile.Read(ref reads),
            Sessions = sessions.Count,
            FirstResponseWritten = Volatile.Read(ref firstResponseWritten) != 0,
        }));

        async Task ServeAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    try
                    {
                        var path = context.Request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        object? value = null;
                        if (context.Request.HttpMethod == "POST" && path is ["start", var attempt] && Guid.TryParse(attempt, out var id))
                        {
                            value = sessions.GetOrAdd(id, _ => new(Guid.NewGuid().ToString(), configuration.Time.AddMinutes(3)));
                            if (Interlocked.Increment(ref starts) == 1)
                            {
                                if (configuration.HoldFirstStart)
                                {
                                    accepted.TrySetResult();
                                    await release.Task.WaitAsync(stop.Token);
                                }
                                Volatile.Write(ref firstResponseWritten, 1);
                            }
                        }
                        else if (context.Request.HttpMethod == "POST" && path is ["read", var reference]
                            && sessions.Values.Any(session => session.Reference == reference))
                        {
                            Interlocked.Increment(ref reads);
                            value = new SelfieResult(SelfieState.Passed, null, "synthetic-policy");
                        }
                        context.Response.StatusCode = value is null ? 404 : 200;
                        context.Response.ContentType = "application/json";
                        if (value is not null)
                        {
                            await JsonSerializer.SerializeAsync(context.Response.OutputStream, value, cancellationToken: stop.Token);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or HttpListenerException) { }
                    finally { context.Response.Close(); }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (HttpListenerException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }
    }

    private sealed record Configuration(DateTimeOffset Time, bool HoldFirstStart);
}
