using System.Net.Sockets;
using System.Text.Json;
using Npgsql;

namespace Duora.Infrastructure.Media;

/// <summary>The end of every storage connection: operation budget, denial reply and the host's stderr line.</summary>
internal static class StorageOperationBoundary
{
    private static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplyBudget = TimeSpan.FromSeconds(1);

    /// <summary>Never throws: the accept loop discards finished tasks, so an escaped failure leaves no trace.</summary>
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
            errors.WriteLine(Describe(error));
            await SendDenialAsync(stream, errors);
        }
    }

    private static string Describe(Exception error) => IsExpectedDenial(error)
        ? $"STORAGE_OPERATION_DENIED {error.GetType().Name}"
        : $"STORAGE_OPERATION_FAILED {error.GetType().Name} {ExceptionFrames.Describe(error)}";

    private static bool IsExpectedDenial(Exception error) => error is IOException or InvalidDataException
        or JsonException or OperationCanceledException or UnauthorizedAccessException or ArgumentException
        or SocketException or FormatException or NpgsqlException or InvalidOperationException;

    private static async Task SendDenialAsync(Stream stream, TextWriter errors)
    {
        try
        {
            using var replyBudget = new CancellationTokenSource(ReplyBudget);
            await PrivateGenerationWire.WriteAsync(stream, new PrivateGenerationReply("denied"), replyBudget.Token);
        }
        catch (Exception replyError)
        {
            errors.WriteLine($"STORAGE_REPLY_LOST {replyError.GetType().Name}");
        }
    }
}
