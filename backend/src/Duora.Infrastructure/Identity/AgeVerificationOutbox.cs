using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Infrastructure.Outbox;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Pedido ao worker para abrir a verificação no fornecedor, gravado na transação de API-P05. Só ids: o worker relê
/// a solicitação no banco, e o nonce nunca passa pela outbox.
/// </summary>
internal sealed record AgeVerificationRequested(Guid VerificationId, Guid LoginSessionId);

internal static class AgeVerificationOutbox
{
    public const string EventType = "identity.age-verification-requested";
    public const string Consumer = "age-verification-provider";

    private const int SchemaVersion = 1;
    private const string ResourceType = "age_verification";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Evento e entrega ao worker. O prazo da entrega é o fim do prazo da solicitação: depois dele abrir no
    /// fornecedor não serve a ninguém.
    /// </summary>
    public static OutboxEvent Create(AgeVerificationRequested message, DateTimeOffset now, DateTimeOffset deadlineAt)
    {
        var identity = new EventIdentity(EventType, SchemaVersion, ResourceType, message.VerificationId);
        var outboxEvent = OutboxEvent.Record(identity, JsonSerializer.Serialize(message, Json), now);
        outboxEvent.AddDelivery(Consumer, message.VerificationId.ToString("D"), deadlineAt);
        return outboxEvent;
    }

    public static bool TryRead(ClaimedDelivery delivery, [NotNullWhen(true)] out AgeVerificationRequested? message)
    {
        message = null;
        if (delivery.EventType != EventType)
        {
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize<AgeVerificationRequested>(delivery.Payload, Json);
        }
        catch (JsonException)
        {
            // Payload ilegível é tratado por quem chama como entrega inválida (Failed), sem retry.
            return false;
        }

        return message is { VerificationId: var id, LoginSessionId: var session } && id != Guid.Empty && session != Guid.Empty;
    }
}
