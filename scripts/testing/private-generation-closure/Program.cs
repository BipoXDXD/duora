using Duora.ClosureProbe;

// Experimental executable only. No project in backend references this assembly.
if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("Linux required; this probe does not qualify other lock/filesystem implementations.");
    return 2;
}

try
{
    var command = args[0];
    var root = Path.GetFullPath(args[1]);
    var area = args[2];
    if (area is not ("candidate" or "promotion"))
    {
        throw new InvalidDataException("Unknown area.");
    }

    var generation = Guid.ParseExact(args[3], "N");
    var domain = Guid.ParseExact(args[4], "N");
    var barrier = new Duora.ClosureProbe.Barrier(args[5], int.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture));
    var protocol = new GenerationProtocol(root, area, generation, domain, barrier);
    if (command == "reserve")
    {
        protocol.ReserveOffline();
        Console.WriteLine("RESERVED");
        return 0;
    }

    if (command == "legacy-write")
    {
        await protocol.WriteAsync(); // Deliberate counterexample: no admission/lock.
        Console.WriteLine("WRITTEN");
        return 0;
    }

    barrier.Wait("before-lock");
    using var lease = protocol.TryAcquire();
    if (lease is null)
    {
        Console.WriteLine("BUSY"); // No evidence, no timeout-based breaking of the lock.
        return 0;
    }

    switch (command)
    {
        case "write":
            protocol.RequireOpen();
            await protocol.WriteAsync();
            Console.WriteLine("WRITTEN");
            break;
        case "close":
            protocol.Close();
            Console.WriteLine("CLOSED"); // Experimental cooperative result; NEVER database evidence.
            break;
        case "sweep":
            await protocol.SweepAsync();
            Console.WriteLine("SWEPT");
            break;
        default:
            throw new InvalidDataException("Unknown command.");
    }

    return 0;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or ArgumentException)
{
    Console.Error.WriteLine($"DENIED {exception.GetType().Name}"); // No keys, paths or generation in logs.
    return 2;
}
