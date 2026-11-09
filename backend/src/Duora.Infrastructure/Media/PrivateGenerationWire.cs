using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

internal sealed record PrivateGenerationRequest(int Version, string Operation,
    PrivateGenerationIdentity? Identity = null, PrivateGenerationManifest? Manifest = null,
    PhotoVariant? Variant = null, long Length = 0, long ClosedRevision = 0, Guid CleanupToken = default,
    string? ObjectKey = null);

internal sealed record PrivateGenerationReply(string Status, long Revision = 0, Guid ProofId = default,
    long Length = 0);

internal static class PrivateGenerationWire
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken) where T : class
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > 16_384)
        {
            throw new InvalidDataException("Invalid private storage frame.");
        }
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty storage frame.");
    }

    /// <summary>Reads a final reply and exactly the body it declares; anything else on the wire is a failure.</summary>
    internal static async Task<(PrivateGenerationReply Reply, byte[] Body)> ReadResponseAsync(Stream stream,
        CancellationToken cancellationToken)
    {
        var reply = await ReadAsync<PrivateGenerationReply>(stream, cancellationToken);
        if (reply.Status is not ("ok" or "missing") || reply.Length is < 0 or > 16_777_216)
        {
            throw new IOException("Private storage did not confirm the operation.");
        }
        var bytes = new byte[checked((int)reply.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            throw new InvalidDataException("Unexpected storage response bytes.");
        }
        return (reply, bytes);
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 16_384)
        {
            throw new InvalidDataException("Private storage frame exceeds its envelope.");
        }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
