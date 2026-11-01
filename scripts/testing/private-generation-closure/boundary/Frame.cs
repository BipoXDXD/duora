using System.Buffers.Binary;
using System.Text.Json;

namespace Duora.ClosureBoundary;

internal static class Frame
{
    internal static async Task<ProbeRequest> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > 4096)
        {
            throw new InvalidDataException("Invalid frame.");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<ProbeRequest>(bytes, Wire.Options) ?? throw new JsonException("Empty request.");
    }

    internal static async Task WriteAsync(Stream stream, ProbeReply reply, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, Wire.Options);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
