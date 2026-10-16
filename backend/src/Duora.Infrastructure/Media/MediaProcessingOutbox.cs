using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Infrastructure.Outbox;

namespace Duora.Infrastructure.Media;

/// <summary>
/// Pedido de processamento de um upload em quarentena (ADR-0025). O payload leva só o id: o Worker relê o resto do
/// banco, e a chave do objeto nunca vai para a outbox (TM-42).
/// </summary>
internal sealed record UploadQuarantined(Guid UploadId);

internal static class MediaProcessingOutbox
{
    public const string EventType = "media.upload-quarantined";
    public const string Consumer = "media-processing";

    /// <summary>Mesmo prazo do abandono do upload: depois disso, processar já não faz sentido.</summary>
    public static readonly TimeSpan DeliveryWindow = Upload.AbandonAfterDelay;

    private const int SchemaVersion = 1;
    private const string ResourceType = "upload";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static OutboxEvent Create(Guid uploadId, DateTimeOffset now)
    {
        var identity = new EventIdentity(EventType, SchemaVersion, ResourceType, uploadId);
        var outboxEvent = OutboxEvent.Record(identity, JsonSerializer.Serialize(new UploadQuarantined(uploadId), Json), now);
        outboxEvent.AddDelivery(Consumer, uploadId.ToString("D"), now + DeliveryWindow);
        return outboxEvent;
    }

    public static bool TryRead(ClaimedDelivery delivery, [NotNullWhen(true)] out UploadQuarantined? message)
    {
        message = null;
        if (delivery.EventType != EventType)
        {
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize<UploadQuarantined>(delivery.Payload, Json);
        }
        catch (JsonException)
        {
            // Payload ilegível vira entrega inválida (Failed) em quem chama, sem retry.
            return false;
        }

        return message is not null && message.UploadId != Guid.Empty;
    }
}
