using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// The boundary has no Linux dependency, so this is what the macOS run and CI can prove. The real host, peer
/// credentials and engine are proved by <see cref="StorageAuthorityRuntimeChecks"/>, which CI does not run.
/// </summary>
public sealed class StorageOperationBoundaryTests
{
    private const string PrivateText = "CANARY-private-generation-detail";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(typeof(NullReferenceException), "NullReferenceException")]
    [InlineData(typeof(NotSupportedException), "NotSupportedException")]
    [InlineData(typeof(InvalidCastException), "InvalidCastException")]
    [InlineData(typeof(KeyNotFoundException), "KeyNotFoundException")]
    [InlineData(typeof(OutOfMemoryException), "OutOfMemoryException")]
    [InlineData(typeof(Exception), "Exception")]
    public async Task AFailureOutsideTheDenialListIsRecordedWithItsFramesAndDeniedToTheClient(Type type, string name)
    {
        using var stream = new MemoryStream();
        using var errors = new StringWriter();
        var failure = (Exception)Activator.CreateInstance(type, PrivateText)!;

        await StorageOperationBoundary.RunAsync(stream, errors, _ => FailInsideTheOperationAsync(failure), TimeProvider.System, Ct);

        var line = errors.ToString();
        Assert.StartsWith($"STORAGE_OPERATION_FAILED {name} ", line, StringComparison.Ordinal);
        Assert.Contains(nameof(FailInsideTheOperationAsync), line, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateText, line, StringComparison.Ordinal);
        Assert.Equal("denied", await ReplyAsync(stream));
    }

    [Theory]
    [InlineData(typeof(IOException), "IOException")]
    [InlineData(typeof(EndOfStreamException), "EndOfStreamException")]
    [InlineData(typeof(InvalidDataException), "InvalidDataException")]
    [InlineData(typeof(JsonException), "JsonException")]
    [InlineData(typeof(OperationCanceledException), "OperationCanceledException")]
    [InlineData(typeof(UnauthorizedAccessException), "UnauthorizedAccessException")]
    [InlineData(typeof(ArgumentException), "ArgumentException")]
    [InlineData(typeof(SocketException), "SocketException")]
    [InlineData(typeof(FormatException), "FormatException")]
    [InlineData(typeof(NpgsqlException), "NpgsqlException")]
    [InlineData(typeof(InvalidOperationException), "InvalidOperationException")]
    public async Task AnExpectedDenialKeepsItsLineAndItsReply(Type type, string name)
    {
        using var stream = new MemoryStream();
        using var errors = new StringWriter();
        var failure = (Exception)Activator.CreateInstance(type)!;

        await StorageOperationBoundary.RunAsync(stream, errors, _ => FailInsideTheOperationAsync(failure), TimeProvider.System, Ct);

        Assert.Equal($"STORAGE_OPERATION_DENIED {name}{Environment.NewLine}", errors.ToString());
        Assert.Equal("denied", await ReplyAsync(stream));
    }

    [Theory]
    [InlineData(typeof(NotSupportedException), "NotSupportedException")]
    [InlineData(typeof(IOException), "IOException")]
    public async Task AReplyThatCannotBeWrittenIsRecordedAndEndsTheConnectionQuietly(Type type, string name)
    {
        await using var stream = new UnwritableStream((Exception)Activator.CreateInstance(type)!);
        using var errors = new StringWriter();

        await StorageOperationBoundary.RunAsync(
            stream, errors, _ => throw new InvalidDataException(PrivateText), TimeProvider.System, Ct);

        var newLine = Environment.NewLine;
        Assert.Equal($"STORAGE_OPERATION_DENIED InvalidDataException{newLine}STORAGE_REPLY_LOST {name}{newLine}",
            errors.ToString());
    }

    [Theory]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task ABrokenLogDoesNotCostTheClientItsDenialNorEscapes(Type logFailure)
    {
        using var stream = new MemoryStream();
        await using var errors = new BrokenWriter(logFailure);

        await StorageOperationBoundary.RunAsync(stream, errors, _ => throw new InvalidCastException(PrivateText), TimeProvider.System, Ct);

        Assert.Equal("denied", await ReplyAsync(stream));
    }

    [Fact]
    public async Task ABrokenLogAndALostReplyTogetherStillEndTheConnectionQuietly()
    {
        await using var stream = new UnwritableStream(new IOException());
        await using var errors = new BrokenWriter(typeof(IOException));

        var run = StorageOperationBoundary.RunAsync(
            stream, errors, _ => throw new InvalidCastException(PrivateText), TimeProvider.System, Ct);

        await run;
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ACancelledCallerReachesTheOperationAndTheClientIsStillDenied()
    {
        using var stream = new MemoryStream();
        using var errors = new StringWriter();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await StorageOperationBoundary.RunAsync(stream, errors, budget =>
        {
            budget.ThrowIfCancellationRequested();
            return Task.FromResult(Confirmed([]));
        }, TimeProvider.System, stop.Token);

        Assert.Equal($"STORAGE_OPERATION_DENIED OperationCanceledException{Environment.NewLine}", errors.ToString());
        Assert.Equal("denied", await ReplyAsync(stream));
    }

    [Fact]
    public async Task ACompletedOperationSendsItsReplyAndBodyAndLeavesNoLine()
    {
        using var stream = new MemoryStream();
        using var errors = new StringWriter();

        await StorageOperationBoundary.RunAsync(stream, errors, _ => Task.FromResult(Confirmed([1, 2, 3])), TimeProvider.System, Ct);

        Assert.Empty(errors.ToString());
        stream.Position = 0;
        var (reply, body) = await PrivateGenerationWire.ReadResponseAsync(stream, Ct);
        Assert.Equal("ok", reply.Status);
        Assert.Equal([1, 2, 3], body);
    }

    [Fact]
    public async Task ABodyCutWhereADenialWouldCompleteItIsNeverAcceptedAsConfirmed()
    {
        const int Delivered = 8;
        var body = new byte[Delivered + FrameLength(new PrivateGenerationReply("denied"))];
        Array.Fill(body, (byte)42);
        await using var stream = new CutOffStream(FrameLength(Confirmed(body).Reply) + Delivered, new IOException());
        using var errors = new StringWriter();

        await StorageOperationBoundary.RunAsync(stream, errors, _ => Task.FromResult(Confirmed(body)), TimeProvider.System, Ct);

        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => PrivateGenerationWire.ReadResponseAsync(stream, Ct));
        Assert.Equal(FrameLength(Confirmed(body).Reply) + Delivered, stream.Length);
        Assert.Equal($"STORAGE_REPLY_INTERRUPTED IOException{Environment.NewLine}", errors.ToString());
    }

    [Theory]
    [InlineData(0, typeof(IOException), "IOException")]
    [InlineData(2, typeof(OperationCanceledException), "OperationCanceledException")]
    [InlineData(6, typeof(NotSupportedException), "NotSupportedException")]
    [InlineData(150, typeof(SocketException), "SocketException")]
    public async Task AReplyCutAtAnyPointReachesTheClientAsEof(int delivered, Type type, string name)
    {
        var failure = (Exception)Activator.CreateInstance(type)!;
        await using var stream = new CutOffStream(delivered, failure);
        using var errors = new StringWriter();

        await StorageOperationBoundary.RunAsync(
            stream, errors, _ => Task.FromResult(Confirmed(new byte[1024])), TimeProvider.System, Ct);

        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => PrivateGenerationWire.ReadResponseAsync(stream, Ct));
        Assert.Equal($"STORAGE_REPLY_INTERRUPTED {name}{Environment.NewLine}", errors.ToString());
    }

    [Fact]
    public async Task ABrokenLogDuringACutReplyEndsTheConnectionQuietly()
    {
        await using var stream = new CutOffStream(2, new IOException());
        await using var errors = new BrokenWriter(typeof(IOException));

        await StorageOperationBoundary.RunAsync(stream, errors, _ => Task.FromResult(Confirmed([1, 2, 3])), TimeProvider.System, Ct);

        Assert.Equal(2, stream.Length);
    }

    /// <summary>
    /// The host's 16 slots are held by operations, so the budget is the boundary's own 30 s and not whatever the
    /// caller passes: a caller that never cancels still gets an operation that reaches the budget denied.
    /// </summary>
    [Fact]
    public async Task AnOperationThatReachesTheBudgetIsDeniedWithoutTheCallerCancelling()
    {
        var time = new FakeTimeProvider();
        using var stream = new MemoryStream();
        using var errors = new StringWriter();

        await StorageOperationBoundary.RunAsync(stream, errors, budget =>
        {
            time.Advance(TimeSpan.FromSeconds(30));
            budget.ThrowIfCancellationRequested();
            return Task.FromResult(Confirmed([1, 2, 3]));
        }, time, CancellationToken.None);

        Assert.Equal($"STORAGE_OPERATION_DENIED OperationCanceledException{Environment.NewLine}", errors.ToString());
        Assert.Equal("denied", await ReplyAsync(stream));
    }

    [Fact]
    public async Task AnOperationJustInsideTheBudgetIsNotCut()
    {
        var time = new FakeTimeProvider();
        using var stream = new MemoryStream();
        using var errors = new StringWriter();

        await StorageOperationBoundary.RunAsync(stream, errors, budget =>
        {
            time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
            budget.ThrowIfCancellationRequested();
            return Task.FromResult(Confirmed([1, 2, 3]));
        }, time, CancellationToken.None);

        Assert.Empty(errors.ToString());
        stream.Position = 0;
        var (reply, body) = await PrivateGenerationWire.ReadResponseAsync(stream, Ct);
        Assert.Equal("ok", reply.Status);
        Assert.Equal([1, 2, 3], body);
    }

    /// <summary>
    /// A client that stops reading must not keep one of the host's 16 slots past the operation budget. Here the clock
    /// is the caller's token, which the budget is linked to: it fires inside the write, and the write has to see it
    /// through the token the operation was handed.
    /// </summary>
    [Theory]
    [InlineData(0, "frame length")]
    [InlineData(1, "frame payload")]
    [InlineData(2, "body")]
    public async Task AClientThatStopsReadingLosesTheWriteToTheOperationBudget(int stalledWrite, string part)
    {
        using var stop = new CancellationTokenSource();
        await using var stream = new StalledClientStream(stalledWrite, stop);
        using var errors = new StringWriter();
        var budget = CancellationToken.None;

        await StorageOperationBoundary.RunAsync(stream, errors, token =>
        {
            budget = token;
            return Task.FromResult(Confirmed([1, 2, 3]));
        }, TimeProvider.System, stop.Token);

        Assert.True(stream.Stalled, part);
        Assert.Equal(budget, stream.StalledToken);
        Assert.Equal($"STORAGE_REPLY_INTERRUPTED OperationCanceledException{Environment.NewLine}", errors.ToString());
    }

    [Fact]
    public async Task BytesAfterTheDeclaredBodyAreNeverAcceptedAsAResponse()
    {
        byte[] wire = [0, 0, 0, 26, .. """{"status":"ok","length":3}"""u8, 1, 2, 3, 9];
        using var stream = new MemoryStream(wire);

        await Assert.ThrowsAsync<InvalidDataException>(() => PrivateGenerationWire.ReadResponseAsync(stream, Ct));
    }

    [Fact]
    public async Task ADeniedReplyIsNeverAcceptedAsAResponse()
    {
        byte[] wire = [0, 0, 0, 19, .. """{"status":"denied"}"""u8];
        using var stream = new MemoryStream(wire);

        await Assert.ThrowsAsync<IOException>(() => PrivateGenerationWire.ReadResponseAsync(stream, Ct));
    }

    [Theory]
    [InlineData((byte)27, """{"status":"ok","length":-1}""")]
    [InlineData((byte)33, """{"status":"ok","length":16777217}""")]
    public async Task ADeclaredBodyOutsideTheEnvelopeIsNeverAcceptedAsAResponse(byte frameLength, string frame)
    {
        byte[] wire = [0, 0, 0, frameLength, .. Encoding.UTF8.GetBytes(frame)];
        using var stream = new MemoryStream(wire);

        await Assert.ThrowsAsync<IOException>(() => PrivateGenerationWire.ReadResponseAsync(stream, Ct));
    }

    private static int FrameLength(PrivateGenerationReply reply) =>
        sizeof(int) + JsonSerializer.SerializeToUtf8Bytes(reply, PrivateGenerationWire.Json).Length;

    private static async Task<string> ReplyAsync(MemoryStream stream)
    {
        stream.Position = 0;
        var reply = await PrivateGenerationWire.ReadAsync<PrivateGenerationReply>(stream, Ct);
        Assert.Equal(stream.Length, stream.Position);
        return reply.Status;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(PrivateGenerationReply Reply, byte[] Body)> FailInsideTheOperationAsync(
        Exception failure)
    {
        await Task.Yield();
        throw failure;
    }

    private static (PrivateGenerationReply Reply, byte[] Body) Confirmed(byte[] body) =>
        (new PrivateGenerationReply("ok", Length: body.Length), body);

    /// <summary>Fails once at a byte position and accepts later writes, as a socket with room left would.</summary>
    private sealed class CutOffStream(int capacity, Exception failure) : MemoryStream
    {
        private bool _cut;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_cut || Length + buffer.Length <= capacity)
            {
                await base.WriteAsync(buffer, cancellationToken);
                return;
            }
            await base.WriteAsync(buffer[..(capacity - (int)Length)], cancellationToken);
            _cut = true;
            throw failure;
        }
    }

    /// <summary>
    /// At one write the client's window is full: time runs out there, and only the token handed to the write can end
    /// it. A write holding a token that never fires carries on, which the test sees at once instead of hanging.
    /// </summary>
    private sealed class StalledClientStream(int stalledWrite, CancellationTokenSource clock) : MemoryStream
    {
        private int _writes;

        public bool Stalled { get; private set; }

        public CancellationToken StalledToken { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_writes++ == stalledWrite)
            {
                Stalled = true;
                StalledToken = cancellationToken;
                await clock.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class BrokenWriter(Type failure) : StringWriter
    {
        public override void WriteLine(string? value) =>
            throw (Exception)Activator.CreateInstance(failure, PrivateText)!;
    }

    private sealed class UnwritableStream(Exception failure) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromException(failure);
    }
}
