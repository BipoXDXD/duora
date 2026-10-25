using System.Net;

namespace Duora.CapacityClient;

public static class BoundedExchange
{
    public static async Task<byte[]> SendAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected,
        PhaseBudget budget, bool cleanup, CancellationToken token, Action? admitted = null)
    {
        using var permit = cleanup ? budget.EnterCleanup() : await budget.EnterAsync(token).ConfigureAwait(false);
        admitted?.Invoke();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode != expected)
        {
            var reason = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? LoadStopReason.Authentication : LoadStopReason.Transport;
            throw new LoadRejectedException(reason);
        }
        // Some content adapters ignore cancellation while creating/buffering their stream. Do not wait past the guard.
        // An interrupted exchange is never a confirmed remote cleanup; the caller stops and retains UNKNOWN.
        await using var stream = await response.Content.ReadAsStreamAsync(token).WaitAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(block, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 65536)
            { throw new LoadRejectedException(LoadStopReason.Transport); }
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }
}

public static class LeaseWatch
{
    public static async Task<LoadStopReason?> RunAsync(Action check, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                check();
                await Task.Delay(100, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (LoadRejectedException error) { await lifetime.CancelAsync().ConfigureAwait(false); return error.Reason; }
        catch (OperationCanceledException) { }
        catch (Exception) { await lifetime.CancelAsync().ConfigureAwait(false); return LoadStopReason.Lease; }
        return null;
    }
}
