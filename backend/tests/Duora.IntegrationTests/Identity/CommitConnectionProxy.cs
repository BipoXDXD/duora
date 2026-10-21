using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

// Test-only PostgreSQL framing proxy. It neither records nor emits authentication/query payloads.
internal sealed class CommitConnectionProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<bool> _cut = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _connections = [];
    private readonly string _host;
    private readonly int _port;
    private readonly Task _accept;
    private int _mode;
    private int _commitsToSkip;
    private int _commitAcknowledgements;

    public CommitConnectionProxy(string connection)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not ("localhost" or "127.0.0.1"))
        {
            throw new ArgumentException("Only a disposable loopback database is supported.", nameof(connection));
        }
        _host = settings.Host;
        _port = settings.Port;
        _listener.Start();
        settings.Host = "127.0.0.1";
        settings.Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // Only synthetic loopback traffic is decoded; production TLS/settings are never touched.
        settings.SslMode = SslMode.Disable;
        settings.GssEncryptionMode = GssEncryptionMode.Disable;
        ConnectionString = settings.ConnectionString;
        _accept = AcceptAsync();
    }

    public string ConnectionString { get; }
    public int ForwardedCommitAcknowledgements => Volatile.Read(ref _commitAcknowledgements);

    public void Arm(bool serverCommitted, int commitsToSkip)
    {
        Assert.InRange(commitsToSkip, 0, 1);
        Volatile.Write(ref _commitsToSkip, commitsToSkip);
        Assert.Equal(0, Interlocked.CompareExchange(ref _mode, serverCommitted ? 2 : 1, 0));
    }

    public Task<bool> WaitForCutAsync(CancellationToken ct) => _cut.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(ConnectAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ConnectAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            await upstream.ConnectAsync(_host, _port, stop.Token);
            var source = client.GetStream();
            var destination = upstream.GetStream();
            var outcome = 0;
            var awaitingCommit = 0;
            var front = ForwardClientAsync();
            var back = ForwardServerAsync();
            try
            {
                await Task.WhenAny(front, back);
            }
            finally
            {
                await stop.CancelAsync();
                client.Close();
                upstream.Close();
                await Task.WhenAll(IgnoreDisconnectAsync(front), IgnoreDisconnectAsync(back));
                if (outcome != 0)
                {
                    _cut.TrySetResult(outcome == 2);
                }
            }

            async Task ForwardClientAsync()
            {
                var length = new byte[4];
                await source.ReadExactlyAsync(length, stop.Token);
                var startup = new byte[CheckedLength(length) - 4];
                await source.ReadExactlyAsync(startup, stop.Token);
                await destination.WriteAsync(length, stop.Token);
                await destination.WriteAsync(startup, stop.Token);
                while (await ReadFrameAsync(source, stop.Token) is { } frame)
                {
                    if (frame[0] == (byte)'Q' && Encoding.ASCII.GetString(frame, 5, frame.Length - 5).TrimEnd('\0', ';').Trim() == "COMMIT")
                    {
                        if (Volatile.Read(ref _mode) != 0 && Volatile.Read(ref _commitsToSkip) > 0)
                        {
                            Interlocked.Decrement(ref _commitsToSkip);
                            await destination.WriteAsync(frame, stop.Token);
                            continue;
                        }
                        var mode = Interlocked.Exchange(ref _mode, 0);
                        if (mode == 1)
                        {
                            outcome = 1;
                            return; // Closing upstream without forwarding COMMIT forces rollback.
                        }
                        if (mode == 2)
                        {
                            Volatile.Write(ref awaitingCommit, 1);
                        }
                    }
                    await destination.WriteAsync(frame, stop.Token);
                }
            }

            async Task ForwardServerAsync()
            {
                while (await ReadFrameAsync(destination, stop.Token) is { } frame)
                {
                    if (frame[0] == (byte)'C' && Encoding.ASCII.GetString(frame, 5, frame.Length - 5) == "COMMIT\0")
                    {
                        if (Volatile.Read(ref awaitingCommit) == 1)
                        {
                            outcome = 2;
                            return; // PostgreSQL completed COMMIT, but Npgsql never receives its acknowledgement.
                        }
                        if (Volatile.Read(ref _mode) != 0)
                        {
                            Interlocked.Increment(ref _commitAcknowledgements);
                        }
                    }
                    await source.WriteAsync(frame, stop.Token);
                }
            }
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[5];
        if (await stream.ReadAsync(header.AsMemory(0, 1), ct) == 0)
        {
            return null;
        }
        await stream.ReadExactlyAsync(header.AsMemory(1), ct);
        var length = CheckedLength(header.AsSpan(1));
        var frame = new byte[length + 1];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(5), ct);
        return frame;
    }

    private static int CheckedLength(ReadOnlySpan<byte> bytes)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes);
        return length is >= 4 and <= 1024 * 1024 ? length : throw new InvalidDataException("Unsupported synthetic PostgreSQL frame.");
    }

    private static async Task IgnoreDisconnectAsync(Task task)
    {
        try
        { await task; }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }
}
