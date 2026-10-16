using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Infrastructure.Outbox;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Pedido ao worker para abrir a verificação no fornecedor, gravado na transação de API-P05. Só ids: o worker relê
/// a solicitação no banco, e o nonce nunca passa pela outbox.
/// </summary>
internal sealed record AgeVerificationRequested(Guid VerificationId);

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
    /// fornecedor não serve a ninguém. <paramref name="loginSessionId"/> ainda vai no payload só porque o worker da
    /// release anterior o exige (expand/contract, deploy.md).
    /// </summary>
    public static OutboxEvent Create(
        AgeVerificationRequested message,
        Guid loginSessionId,
        DateTimeOffset now,
        DateTimeOffset deadlineAt)
    {
        var identity = new EventIdentity(EventType, SchemaVersion, ResourceType, message.VerificationId);
        var payload = new Payload(message.VerificationId, loginSessionId);
        var outboxEvent = OutboxEvent.Record(identity, JsonSerializer.Serialize(payload, Json), now);
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

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(delivery.Payload, Json);
        }
        catch (JsonException)
        {
            // Payload ilegível é tratado por quem chama como entrega inválida (Failed), sem retry.
            return false;
        }

        if (payload is not { VerificationId: var id } || id == Guid.Empty)
        {
            return false;
        }

        message = new AgeVerificationRequested(id);
        return true;
    }

    /// <summary>
    /// Forma gravada do evento. <c>LoginSessionId</c> ainda é gravado para o worker da release anterior, que o exige,
    /// e é ignorado (e aceito ausente) na leitura. Sai em dois passos: a gravação na release seguinte, o campo depois
    /// que nenhuma entrada com ele puder estar pendente (deploy.md).
    /// </summary>
    private sealed record Payload(Guid VerificationId, Guid? LoginSessionId);
}
