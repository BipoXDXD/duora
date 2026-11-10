using System.Net.Sockets;
using System.Text.Json;
using Npgsql;

namespace Duora.Infrastructure.Media;

/// <summary>The end of every storage connection: operation budget, denial reply and the host's stderr line.</summary>
internal static class StorageOperationBoundary
{
    private static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplyBudget = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A failed operation ends here: the accept loop discards finished tasks, so nothing it raises may escape. The
    /// client is denied before the log is written, because a broken log must not cost it the reply. The budget runs on
    /// <paramref name="time"/> so its expiry can be proved without waiting 30 s.
    /// </summary>
    internal static async Task RunAsync(Stream stream, TextWriter errors,
        Func<CancellationToken, Task<(PrivateGenerationReply Reply, byte[] Body)>> operation, TimeProvider time,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(OperationBudget, time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        (PrivateGenerationReply Reply, byte[] Body) outcome;
        try
        {
            outcome = await operation(budget.Token);
        }
        catch (Exception error)
        {
            var replyError = await SendDenialAsync(stream);
            Record(errors, log =>
            {
                log.WriteLine(Describe(error));
                if (replyError is not null)
                {
                    log.WriteLine($"STORAGE_REPLY_LOST {replyError.GetType().Name}");
                }
            });
            return;
        }
        await SendReplyAsync(stream, errors, outcome, budget.Token);
    }

    /// <summary>
    /// Once a reply has started, a denial frame would be read as the rest of its body. Closing the connection
    /// leaves the client at EOF, which it never takes for a confirmation.
    /// </summary>
    private static async Task SendReplyAsync(Stream stream, TextWriter errors,
        (PrivateGenerationReply Reply, byte[] Body) outcome, CancellationToken cancellationToken)
    {
        try
        {
            await PrivateGenerationWire.WriteAsync(stream, outcome.Reply, cancellationToken);
            await stream.WriteAsync(outcome.Body, cancellationToken);
        }
        catch (Exception error)
        {
            Record(errors, log => log.WriteLine($"STORAGE_REPLY_INTERRUPTED {error.GetType().Name}"));
        }
    }

    private static string Describe(Exception error) => IsExpectedDenial(error)
        ? $"STORAGE_OPERATION_DENIED {error.GetType().Name}"
        : $"STORAGE_OPERATION_FAILED {error.GetType().Name} {ExceptionFrames.Describe(error)}";

    private static bool IsExpectedDenial(Exception error) => error is IOException or InvalidDataException
        or JsonException or OperationCanceledException or UnauthorizedAccessException or ArgumentException
        or SocketException or FormatException or NpgsqlException or InvalidOperationException;

    private static async Task<Exception?> SendDenialAsync(Stream stream)
    {
        try
        {
            using var replyBudget = new CancellationTokenSource(ReplyBudget);
            await PrivateGenerationWire.WriteAsync(stream, new PrivateGenerationReply("denied"), replyBudget.Token);
            return null;
        }
        catch (Exception replyError)
        {
            return replyError;
        }
    }

    private static void Record(TextWriter errors, Action<TextWriter> write)
    {
        try
        {
            write(errors);
        }
        catch (Exception)
        {
            // This writer is the host's only channel: a failure to write to it has nowhere else to go.
        }
    }
}
