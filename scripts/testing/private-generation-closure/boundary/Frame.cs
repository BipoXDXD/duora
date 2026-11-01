using System.Buffers.Binary;
using System.Text.Json;

namespace Duora.ClosureBoundary;

internal static class Frame
{
    internal static Task<ProbeRequest> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadObjectAsync<ProbeRequest>(stream, cancellationToken);

    internal static async Task<T> ReadObjectAsync<T>(Stream stream, CancellationToken cancellationToken) where T : class
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
        return JsonSerializer.Deserialize<T>(bytes, Wire.Options) ?? throw new JsonException("Empty request.");
    }

    internal static Task WriteAsync(Stream stream, ProbeReply reply, CancellationToken cancellationToken) =>
        WriteObjectAsync(stream, reply, cancellationToken);

    internal static async Task WriteObjectAsync(Stream stream, object reply, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, reply.GetType(), Wire.Options);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
