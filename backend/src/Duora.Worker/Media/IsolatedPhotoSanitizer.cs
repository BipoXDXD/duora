using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Media;
using Duora.MediaProtocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Duora.Worker.Media;

/// <summary>Candidate transport envelope, not a production capacity approval (ADR-0036 P174-3/4).</summary>
internal sealed class IsolatedPhotoSanitizerOptions
{
    public string LauncherSocket { get; set; } = "";
    public int LauncherUid { get; set; }
    public string ProfileSha256 { get; set; } = "";
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(50);
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ControlTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan StorageCommitMargin { get; set; } = TimeSpan.FromSeconds(60);

    public bool IsConsistent() => TotalTimeout > CleanupTimeout
        && TotalTimeout <= TimeSpan.FromMilliseconds(ProtocolBudget.Candidate.DeadlineMilliseconds)
        && CleanupTimeout > TimeSpan.Zero && CleanupTimeout <= TimeSpan.FromSeconds(10)
        && ControlTimeout > TimeSpan.Zero && ControlTimeout <= CleanupTimeout
        && StorageCommitMargin >= TimeSpan.FromSeconds(60);

    public bool IsConfigured() => Path.IsPathFullyQualified(LauncherSocket)
        && !LauncherSocket.Contains('\0')
        && Encoding.UTF8.GetByteCount(LauncherSocket) < 100
        && LauncherUid > 0 && ProfileSha256.Length == 64 && ProfileSha256.All(Uri.IsHexDigit);
}

/// <summary>
/// Only the fixed S2 Unix control protocol is available here. No decoder, runtime API, image selection or fallback.
/// DMLR exited proves termination; the bounded S1 parser independently validates every result before publication.
/// </summary>
internal sealed partial class IsolatedPhotoSanitizer(
    IsolatedPhotoSanitizerOptions options,
    ILogger<IsolatedPhotoSanitizer>? logger = null) : IPhotoSanitizer, IDisposable
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly ILogger<IsolatedPhotoSanitizer> _logger = logger ?? NullLogger<IsolatedPhotoSanitizer>.Instance;
    private byte[]? _unconfirmed;
    private int _disposed;

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!await _slot.WaitAsync(0, cancellationToken))
        {
            throw Unavailable();
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ControlTimeout);
            await ConfirmPreviousAsync(timeout.Token);
            if (await ControlAsync(2, RandomNumberGenerator.GetBytes(16), timeout.Token) != 5)
            {
                throw Unavailable();
            }
        }
        finally
        {
            _slot.Release();
        }
    }

    public async Task<SanitizeOutcome> SanitizeAsync(PhotoFormat declared, ReadOnlyMemory<byte> original,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (original.Length > PhotoLimits.MaxBytes)
        {
            return new SanitizeOutcome.Rejected(PhotoRejection.ProcessingLimit);
        }
        if (!await _slot.WaitAsync(0, cancellationToken))
        {
            throw Unavailable();
        }
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.TotalTimeout - options.CleanupTimeout);
            await ConfirmPreviousAsync(timeout.Token);
            var remaining = options.TotalTimeout - options.CleanupTimeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                throw Unavailable();
            }
            var request = new SanitizerRequest(RandomNumberGenerator.GetBytes(16), declared, original.ToArray(),
                ProtocolBudget.Candidate with { DeadlineMilliseconds = (int)remaining.TotalMilliseconds });
            var outcome = await StartAsync(request, timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return outcome;
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            // Closing the start connection is not cancellation. Use the separate restricted control operation,
            // within the reserved portion of the same total envelope; uncertain cleanup blocks the next start.
            var remaining = options.TotalTimeout - Stopwatch.GetElapsedTime(started);
            if (_unconfirmed is not null && remaining > TimeSpan.Zero)
            {
                using var cleanup = new CancellationTokenSource(remaining < options.CleanupTimeout ? remaining : options.CleanupTimeout);
                try
                {
                    await ControlAsync(3, _unconfirmed, cleanup.Token);
                    while (_unconfirmed is not null)
                    {
                        if (await ControlAsync(2, _unconfirmed, cleanup.Token) == 7)
                        {
                            _unconfirmed = null;
                            break;
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(25), cleanup.Token);
                    }
                }
                catch (Exception cleanupError) when (cleanupError is IOException or SocketException or OperationCanceledException)
                {
                    // Keep the opaque pending id; never turn an unknown/running/blocked status into success.
                }
            }
            if (_unconfirmed is not null)
            {
                LogUnconfirmed(_logger);
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw Unavailable();
        }
        finally
        {
            _slot.Release();
        }
    }

    private async Task ConfirmPreviousAsync(CancellationToken cancellationToken)
    {
        if (_unconfirmed is null)
        {
            return;
        }
        if (await ControlAsync(2, _unconfirmed, cancellationToken) != 7)
        {
            throw Unavailable();
        }
        _unconfirmed = null;
    }

    private async Task<SanitizeOutcome> StartAsync(SanitizerRequest request, CancellationToken cancellationToken)
    {
        using var socket = await ConnectAsync(cancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _unconfirmed = request.Nonce;
        var write = WriteAsync();
        try
        {
            // A full launcher can answer before reading the original. Read concurrently so a refused large
            // request cannot deadlock in the send buffer or obscure the valid busy response with a broken pipe.
            var code = await ReadHeaderAsync(stream, request.Nonce, cancellationToken);
            if (code == 0)
            {
                await write;
                _unconfirmed = null; // Authenticated launcher confirms exit and cleanup, not image correctness.
                return await SanitizerProtocol.ReadResponseAsync(stream, request, cancellationToken);
            }
            await RequireEndAsync(stream, cancellationToken);
            if (code is 1 or 3 or 4)
            {
                _unconfirmed = null;
            }
            throw Unavailable();
        }
        finally
        {
            await sending.CancelAsync();
            socket.Dispose();
            try
            {
                await write;
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The primary response/failure determines the result; observe the stopped writer as well.
            }
        }

        async Task WriteAsync()
        {
            await stream.WriteAsync(Header(1, request.Nonce), sending.Token);
            await SanitizerProtocol.WriteRequestAsync(stream, request, sending.Token);
            socket.Shutdown(SocketShutdown.Send);
        }
    }

    private async Task<byte> ControlAsync(byte operation, byte[] id, CancellationToken cancellationToken)
    {
        using var socket = await ConnectAsync(cancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(Header(operation, id), cancellationToken);
        socket.Shutdown(SocketShutdown.Send);
        var code = await ReadHeaderAsync(stream, id, cancellationToken);
        await RequireEndAsync(stream, cancellationToken);
        return code;
    }

    private async Task<Socket> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || !options.IsConsistent() || !options.IsConfigured())
        {
            throw Unavailable();
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.LauncherSocket), cancellationToken);
            Span<byte> credentials = stackalloc byte[12];
            // Linux SOL_SOCKET/SO_PEERCRED: native pid_t, uid_t, gid_t. Verify before sending original bytes.
            if (socket.GetRawSocketOption(1, 17, credentials) != credentials.Length
                || BitConverter.ToUInt32(credentials[4..8]) != (uint)options.LauncherUid)
            {
                throw Unavailable();
            }
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<byte> ReadHeaderAsync(Stream stream, byte[] id, CancellationToken cancellationToken)
    {
        var header = new byte[54];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, 5).SequenceEqual("DMLR\x01"u8) || header[5] > 8
            || !CryptographicOperations.FixedTimeEquals(header.AsSpan(6, 16), id)
            || !CryptographicOperations.FixedTimeEquals(header.AsSpan(22, 32), Convert.FromHexString(options.ProfileSha256)))
        {
            throw Unavailable();
        }
        return header[5];
    }

    private static byte[] Header(byte operation, byte[] id) => [.. "DMLC\x01"u8, operation, .. id];

    private static async Task RequireEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            throw Unavailable();
        }
    }

    private static IOException Unavailable() => new("Media Launcher unavailable or incompatible; no isolated result accepted.");

    [LoggerMessage(Level = LogLevel.Error, Message = "Media launcher termination could not be confirmed; another attempt requires confirmed cleanup.")]
    private static partial void LogUnconfirmed(ILogger logger);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _slot.Dispose();
        }
    }
}
