using Duora.Infrastructure.Media;

if (args.Length != 2 || args[0] != "--configuration")
{
    Console.Error.WriteLine("Usage: Duora.StorageAuthority --configuration <protected-file>");
    return 2;
}
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
try
{
    await StorageAuthorityHost.RunAsync(args[1], shutdown.Token);
    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException
    or PlatformNotSupportedException or UnauthorizedAccessException or System.Text.Json.JsonException
    or Npgsql.NpgsqlException)
{
    Console.Error.WriteLine($"STORAGE_AUTHORITY_STARTUP_DENIED {error.GetType().Name}");
    return 2;
}
