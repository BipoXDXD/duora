using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Duora.Infrastructure.Media;
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

        await StorageOperationBoundary.RunAsync(stream, errors, _ => FailInsideTheOperationAsync(failure), Ct);

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

        await StorageOperationBoundary.RunAsync(stream, errors, _ => FailInsideTheOperationAsync(failure), Ct);

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
            stream, errors, _ => throw new InvalidDataException(PrivateText), Ct);

        var newLine = Environment.NewLine;
        Assert.Equal($"STORAGE_OPERATION_DENIED InvalidDataException{newLine}STORAGE_REPLY_LOST {name}{newLine}",
            errors.ToString());
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
            return Task.CompletedTask;
        }, stop.Token);

        Assert.Equal($"STORAGE_OPERATION_DENIED OperationCanceledException{Environment.NewLine}", errors.ToString());
        Assert.Equal("denied", await ReplyAsync(stream));
    }

    [Fact]
    public async Task ACompletedOperationLeavesNoLineAndNoReply()
    {
        using var stream = new MemoryStream();
        using var errors = new StringWriter();

        await StorageOperationBoundary.RunAsync(stream, errors, _ => Task.CompletedTask, Ct);

        Assert.Empty(errors.ToString());
        Assert.Equal(0, stream.Length);
    }

    private static async Task<string> ReplyAsync(MemoryStream stream)
    {
        stream.Position = 0;
        var reply = await PrivateGenerationWire.ReadAsync<PrivateGenerationReply>(stream, Ct);
        Assert.Equal(stream.Length, stream.Position);
        return reply.Status;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FailInsideTheOperationAsync(Exception failure)
    {
        await Task.Yield();
        throw failure;
    }

    private sealed class UnwritableStream(Exception failure) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromException(failure);
    }
}
