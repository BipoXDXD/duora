namespace Duora.ClosureProbe;

internal sealed class Barrier(string point, int variant)
{
    internal void Wait(string here, int kind = 0)
    {
        if (point == "fail-" + here && variant == kind)
        {
            throw new IOException("Injected failure before sync; not a power-loss simulation.");
        }

        if (point == here && variant == kind)
        {
            Console.WriteLine("READY");
            Console.Out.Flush();
            if (Console.ReadLine() != "go")
            {
                throw new IOException("Barrier input lost.");
            }
        }
    }
}

// Real adapter pauses on its exact-length EOF probe, after writing the payload to .partial.
internal sealed class PausedInput(Barrier barrier, int variant) : MemoryStream(Payload(), writable: false)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position == Length)
        {
            barrier.Wait("eof", variant);
        }

        return base.ReadAsync(buffer, cancellationToken);
    }

    private static byte[] Payload() => Enumerable.Range(0, 32768).SelectMany(_ => new byte[] { 1, 2, 3 }).ToArray();
}
