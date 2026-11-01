using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Duora.ClosureBoundary;

[assembly: SupportedOSPlatform("linux")]

if (!OperatingSystem.IsLinux() || args.Length != 3 || args[0] is not ("authority" or "controller"))
{
    Console.Error.WriteLine("DENIED unsupported invocation");
    return 2;
}

var role = args[0];
var expectedUid = role == "authority" ? 17010U : 17030U;
if (LinuxBoundary.CurrentUid != expectedUid || LinuxBoundary.CurrentGid != expectedUid)
{
    Console.Error.WriteLine("DENIED process identity");
    return 2;
}

try
{
    var layout = new ProtectedLayout(role == "authority" ? "/state" : "/witness-state", expectedUid);
    var barrier = new Duora.ClosureProbe.Barrier(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
    var authority = new Authority(layout, barrier);
    var controller = new RevocationController(layout, barrier);
    var socketPath = $"/state/rpc/{role}/service.sock";
    File.Delete(socketPath);
    using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    listener.Bind(new UnixDomainSocketEndPoint(socketPath));
    // Connection permission is deliberately broad: each endpoint authenticates SO_PEERCRED.
    // The root-provisioned RPC parents prevent clients from replacing the server endpoint.
    File.SetUnixFileMode(socketPath, (UnixFileMode)438);
    listener.Listen(16);
    Console.WriteLine(JsonSerializer.Serialize(new { status = "LISTENING", pid = Environment.ProcessId }));
    while (true)
    {
        var socket = await listener.AcceptAsync();
        _ = Task.Run(async () =>
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var parsed = false;
            try
            {
                var peer = LinuxBoundary.Peer(socket);
                var request = await Frame.ReadAsync(stream, timeout.Token);
                parsed = true;
                object reply = role == "authority" ? await authority.HandleAsync(peer, request, stream, timeout.Token)
                    : controller.Handle(peer, request);
                await Frame.WriteObjectAsync(stream, reply, timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException or UnauthorizedAccessException or ArgumentException or SocketException or FormatException)
            {
                Console.Error.WriteLine($"DENIED {exception.GetType().Name}");
                try
                {
                    object failure = role == "authority"
                        ? new ProbeReply(!parsed ? ReplyCode.Denied : ReplyCode.RecoveryRequired)
                        : new WitnessReply(!parsed ? WitnessStatus.Denied : WitnessStatus.RecoveryRequired);
                    await Frame.WriteObjectAsync(stream, failure, CancellationToken.None);
                }
                catch (Exception writeError) when (writeError is IOException or SocketException or ObjectDisposedException)
                {
                    Console.Error.WriteLine($"REPLY_FAILED {writeError.GetType().Name}");
                }
            }
        });
    }
}
catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException or FormatException)
{
    Console.Error.WriteLine($"STARTUP_DENIED {error.GetType().Name}");
    return 2;
}
