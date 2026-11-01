using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Npgsql;

namespace Duora.Infrastructure.Media;

/// <summary>Standalone Linux host. Never instantiated or credentialed by Web/Worker composition.</summary>
public static class StorageAuthorityHost
{
    public static Task RunAsync(string configurationPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Storage authority requires Linux.");
        }
        return RunLinuxAsync(configurationPath, cancellationToken);
    }

    [SupportedOSPlatform("linux")]
    private static async Task RunLinuxAsync(string configurationPath, CancellationToken cancellationToken)
    {
        var settings = ReadTrusted<Settings>(configurationPath);
        if (settings.Environment != "Testing")
        {
            throw new InvalidOperationException("Storage authority is unqualified outside Testing.");
        }
        var layout = new PrivateGenerationLayout(settings.Root);
        using var lifetime = layout.OpenControl("domain.lock");
        if (!PrivateGenerationLinux.TryLock(lifetime))
        {
            throw new IOException("Storage authority is already active.");
        }
        layout.Validate();
        var businessConnection = ReadSecret(settings.BusinessConnectionFile);
        var referenceConnection = ReadSecret(settings.ReferenceConnectionFile);
        var businessIdentity = new NpgsqlConnectionStringBuilder(businessConnection);
        var referenceIdentity = new NpgsqlConnectionStringBuilder(referenceConnection);
        if (businessIdentity.Host == referenceIdentity.Host && businessIdentity.Port == referenceIdentity.Port
            && businessIdentity.Database == referenceIdentity.Database)
        {
            throw new InvalidDataException("Storage reference cannot be the business database.");
        }
        await using var business = NpgsqlDataSource.Create(businessConnection);
        await using var referenceSource = NpgsqlDataSource.Create(referenceConnection);
        var reference = new PrivateGenerationReference(referenceSource, layout.Enrollment.Domain,
            layout.Enrollment.Incarnation, Guid.NewGuid());
        new PrivateGenerationJournal(layout).Synchronize();
        await reference.AttachAsync(cancellationToken);
        using var engine = new PrivateGenerationAuthorityEngine(layout, reference, new(business));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        File.Delete(layout.SocketPath);
        listener.Bind(new UnixDomainSocketEndPoint(layout.SocketPath));
        File.SetUnixFileMode(layout.SocketPath, (UnixFileMode)438);
        listener.Listen(16);
        using var slots = new SemaphoreSlim(16);
        var active = new HashSet<Task>();
        Console.WriteLine("STORAGE_AUTHORITY_READY");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await slots.WaitAsync(cancellationToken);
                Socket socket;
                try
                {
                    socket = await listener.AcceptAsync(cancellationToken);
                }
                catch
                {
                    slots.Release();
                    throw;
                }
                var operation = ServeAsync(socket, engine, cancellationToken);
                active.Add(operation);
                active.RemoveWhere(task => task.IsCompleted);
                _ = operation.ContinueWith(_ => slots.Release(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            // The lifetime lock is held until every operation, including cancelled I/O, has actually ended.
            await Task.WhenAll(active);
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task ServeAsync(Socket socket, PrivateGenerationAuthorityEngine engine,
        CancellationToken cancellationToken)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var peer = PrivateGenerationLinux.Peer(socket);
            var request = await PrivateGenerationWire.ReadAsync<PrivateGenerationRequest>(stream, budget.Token);
            var (reply, body) = await engine.HandleAsync(peer, request, stream, budget.Token);
            await PrivateGenerationWire.WriteAsync(stream, reply, budget.Token);
            await stream.WriteAsync(body, budget.Token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException
            or OperationCanceledException or UnauthorizedAccessException or ArgumentException
            or SocketException or FormatException or NpgsqlException or InvalidOperationException)
        {
            Console.Error.WriteLine($"STORAGE_OPERATION_DENIED {error.GetType().Name}");
            try
            {
                using var replyBudget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await PrivateGenerationWire.WriteAsync(stream, new PrivateGenerationReply("denied"), replyBudget.Token);
            }
            catch (Exception replyError) when (replyError is IOException or SocketException
                or ObjectDisposedException or OperationCanceledException)
            {
                Console.Error.WriteLine($"STORAGE_REPLY_LOST {replyError.GetType().Name}");
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static T ReadTrusted<T>(string path) where T : class
    {
        using var directory = PrivateGenerationLinux.OpenDirectory(Path.GetDirectoryName(path)!);
        using var file = PrivateGenerationLinux.OpenChild(directory, Path.GetFileName(path), directory: false);
        var info = PrivateGenerationLinux.Stat(file);
        if (info.Uid != 0 || info.Gid != PrivateGenerationLinux.CurrentGid || (info.Mode & 0xfff) != 288 || info.Links != 1)
        {
            throw new InvalidDataException("Untrusted authority configuration.");
        }
        return PrivateGenerationLinux.ReadJson<T>(file);
    }

    [SupportedOSPlatform("linux")]
    private static string ReadSecret(string path) => ReadTrusted<ConnectionSecret>(path).ConnectionString;

    private sealed record Settings(string Environment, string Root, string BusinessConnectionFile,
        string ReferenceConnectionFile);
    private sealed record ConnectionSecret(string ConnectionString);
}
