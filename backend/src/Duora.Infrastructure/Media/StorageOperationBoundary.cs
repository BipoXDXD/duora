using System.Net.Sockets;
using System.Text.Json;
using Npgsql;

namespace Duora.Infrastructure.Media;

/// <summary>The end of every storage connection: operation budget, denial reply and the host's stderr line.</summary>
internal static class StorageOperationBoundary
{
    private static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplyBudget = TimeSpan.FromSeconds(1);

    internal static async Task RunAsync(Stream stream, TextWriter errors, Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(OperationBudget);
        try
        {
            await operation(budget.Token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException
            or OperationCanceledException or UnauthorizedAccessException or ArgumentException
            or SocketException or FormatException or NpgsqlException or InvalidOperationException)
        {
            errors.WriteLine($"STORAGE_OPERATION_DENIED {error.GetType().Name}");
            try
            {
                using var replyBudget = new CancellationTokenSource(ReplyBudget);
                await PrivateGenerationWire.WriteAsync(stream, new PrivateGenerationReply("denied"), replyBudget.Token);
            }
            catch (Exception replyError) when (replyError is IOException or SocketException
                or ObjectDisposedException or OperationCanceledException)
            {
                errors.WriteLine($"STORAGE_REPLY_LOST {replyError.GetType().Name}");
            }
        }
    }
}
