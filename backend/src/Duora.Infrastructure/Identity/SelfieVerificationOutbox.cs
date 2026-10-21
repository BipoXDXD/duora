using Duora.Infrastructure.Outbox;

namespace Duora.Infrastructure.Identity;

internal static class SelfieVerificationOutbox
{
    public const string Consumer = "selfie-provider";
    public const string EventType = "identity.selfie-requested";

    public static OutboxEvent Create(Guid id, DateTimeOffset now, DateTimeOffset deadline)
    {
        var identity = new EventIdentity(EventType, 1, "selfie_verification", id);
        var item = OutboxEvent.Record(identity, System.Text.Json.JsonSerializer.Serialize(id), now);
        item.AddDelivery(Consumer, id.ToString("D"), deadline);
        return item;
    }
}
