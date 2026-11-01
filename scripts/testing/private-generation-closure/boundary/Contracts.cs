using System.Text.Json;
using System.Text.Json.Serialization;

namespace Duora.ClosureBoundary;

internal sealed record DomainIdentity(Guid Domain, long Epoch, int ProtocolVersion);
internal sealed record WriteGrant(DomainIdentity Domain, Guid Generation, string Area, string Nonce);
internal sealed record PeerIdentity(int Uid, int Gid, int Pid);
internal enum ReplyCode { Written, Busy, Denied, AdmissionBlocked, RecoveryRequired, ClosedExperimental, SweptExperimental }
internal sealed record ProbeRequest(int Version, string Operation, WriteGrant? Grant, string? Variant, long Length);
internal sealed record ProbeReply(ReplyCode Code, long Revision = 0);
internal sealed record NodeIdentity(ulong Inode, uint DeviceMajor, uint DeviceMinor);
internal sealed record GenerationRegistration(Guid Generation, string Area, string Nonce, bool Committed);
internal sealed record Enrollment(Guid Domain, long Epoch, int ProtocolVersion, NodeIdentity Root,
    NodeIdentity Control, Dictionary<string, NodeIdentity> Files, Dictionary<string, NodeIdentity> Data,
    GenerationRegistration[] Generations)
{
    internal DomainIdentity Identity => new(Domain, Epoch, ProtocolVersion);
}

internal static class Wire
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new GuidHexConverter(), new JsonStringEnumConverter() },
    };
}

internal sealed class GuidHexConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (!Guid.TryParseExact(value, "N", out var parsed) || parsed.ToString("N") != value)
        {
            throw new JsonException("Invalid identifier.");
        }

        return parsed;
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("N"));
}
