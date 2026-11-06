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
    /// client is denied before the log is written, because a broken log must not cost it the reply.
    /// </summary>
    internal static async Task RunAsync(Stream stream, TextWriter errors, Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(OperationBudget);
        try
        {
            await operation(budget.Token);
        }
        catch (Exception error)
        {
            var replyError = await SendDenialAsync(stream);
            Record(errors, error, replyError);
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

    private static void Record(TextWriter errors, Exception error, Exception? replyError)
    {
        try
        {
            errors.WriteLine(Describe(error));
            if (replyError is not null)
            {
                errors.WriteLine($"STORAGE_REPLY_LOST {replyError.GetType().Name}");
            }
        }
        catch (Exception)
        {
            // This writer is the host's only channel: a failure to write to it has nowhere else to go.
        }
    }
}
