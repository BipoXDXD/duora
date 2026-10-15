using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Infrastructure.Outbox;

namespace Duora.Infrastructure.Email;

internal enum AccountEmailKind
{
    EmailConfirmation,
    PasswordReset,
    RegistrationAttemptOnExistingAccount,
    PendingRegistrationReminder,
    PasswordResetForUnknownAddress,
}

/// <summary>
/// Pedido de e-mail de conta, gravado na outbox na transação do cadastro ou da recuperação. O token já vem no
/// formato do link (base64url); o payload é esvaziado quando a entrega termina. <c>Language</c> é a tag do idioma
/// (<c>pt-BR</c> ou <c>en</c>) escolhida na requisição que pediu o e-mail. É um campo aditivo: payload gravado antes
/// dele não o tem, e sem ele (ou com tag fora da allowlist) o e-mail sai no idioma padrão.
/// </summary>
internal sealed record AccountEmailRequested(AccountEmailKind Kind, string To, Guid UserId, string? Token, string? Language);

internal static class AccountEmailOutbox
{
    public const string EventType = "identity.account-email-requested";
    public const string Consumer = "transactional-email";

    private const int SchemaVersion = 1;
    private const string ResourceType = "user";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Até quando o e-mail ainda faz sentido. Fica bem abaixo da validade do token do link, para a pessoa ter
    /// tempo de usá-lo; depois disso a entrega expira em vez de chegar com link morto.
    /// </summary>
    public static TimeSpan DeliveryWindow(AccountEmailKind kind) => kind switch
    {
        AccountEmailKind.EmailConfirmation => TimeSpan.FromHours(12),
        AccountEmailKind.PasswordReset => TimeSpan.FromMinutes(15),
        AccountEmailKind.RegistrationAttemptOnExistingAccount
            or AccountEmailKind.PendingRegistrationReminder
            or AccountEmailKind.PasswordResetForUnknownAddress => TimeSpan.FromHours(24),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Tipo de e-mail desconhecido."),
    };

    public static OutboxEvent Create(AccountEmailRequested message, DateTimeOffset now)
    {
        var identity = new EventIdentity(EventType, SchemaVersion, ResourceType, message.UserId);
        var outboxEvent = OutboxEvent.Record(identity, JsonSerializer.Serialize(message, Json), now);
        outboxEvent.AddDelivery(Consumer, message.UserId.ToString("D"), now + DeliveryWindow(message.Kind));
        return outboxEvent;
    }

    public static bool TryRead(ClaimedDelivery delivery, [NotNullWhen(true)] out AccountEmailRequested? message)
    {
        message = null;
        if (delivery.EventType != EventType)
        {
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize<AccountEmailRequested>(delivery.Payload, Json);
        }
        catch (JsonException)
        {
            // Payload ilegível é tratado por quem chama como entrega inválida (Failed), sem retry.
            return false;
        }

        return message is not null;
    }
}
