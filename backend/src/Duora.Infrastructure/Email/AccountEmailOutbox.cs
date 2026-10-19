using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Core.Notifications;
using Duora.Infrastructure.Outbox;

namespace Duora.Infrastructure.Email;

internal enum AccountEmailKind
{
    EmailConfirmation,
    PasswordReset,
    RegistrationAttemptOnExistingAccount,
    PendingRegistrationReminder,
    PasswordResetForUnknownAddress,
    MfaEnabledNotice,
    MfaDisabledNotice,
    RecoveryCodesRegeneratedNotice,
    PasswordChangedNotice,
    PasswordResetNotice,

    /// <summary>
    /// Aviso sem link ao pedido de reset de conta privilegiada sem MFA (I5). O leitor foi a deploy antes do gravador
    /// (expand/contract, model.md); a gravação veio com o bootstrap administrativo.
    /// </summary>
    PasswordResetUnavailable,
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
            or AccountEmailKind.PasswordResetForUnknownAddress
            or AccountEmailKind.PasswordResetUnavailable => TimeSpan.FromHours(24),

        // Aviso de credencial não tem link que expire e continua útil enquanto a pessoa puder reagir: 72 h
        // (hipótese) cobrem um fim de semana de worker parado sem mandar um aviso velho demais para fazer sentido.
        AccountEmailKind.MfaEnabledNotice
            or AccountEmailKind.MfaDisabledNotice
            or AccountEmailKind.RecoveryCodesRegeneratedNotice
            or AccountEmailKind.PasswordChangedNotice
            or AccountEmailKind.PasswordResetNotice => TimeSpan.FromHours(72),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Tipo de e-mail desconhecido."),
    };

    /// <summary>Tipo de cada aviso de credencial no payload; a única tabela entre os dois enums.</summary>
    private static readonly Dictionary<SecurityChange, AccountEmailKind> NoticeKinds = new()
    {
        [SecurityChange.MfaEnabled] = AccountEmailKind.MfaEnabledNotice,
        [SecurityChange.MfaDisabled] = AccountEmailKind.MfaDisabledNotice,
        [SecurityChange.RecoveryCodesRegenerated] = AccountEmailKind.RecoveryCodesRegeneratedNotice,
        [SecurityChange.PasswordChanged] = AccountEmailKind.PasswordChangedNotice,
        [SecurityChange.PasswordReset] = AccountEmailKind.PasswordResetNotice,
    };

    /// <summary>A mudança de credencial que o tipo avisa, ou <c>null</c> se o tipo não é aviso.</summary>
    public static SecurityChange? SecurityChangeOf(AccountEmailKind kind) =>
        NoticeKinds.Where(pair => pair.Value == kind).Select(pair => (SecurityChange?)pair.Key).SingleOrDefault();

    /// <summary>
    /// Aviso de credencial ao titular (TM-21), gravado na transação da mudança. Sem token: o aviso nunca leva
    /// credencial, e o worker recusa o que levar.
    /// </summary>
    public static OutboxEvent CreateSecurityNotice(
        SecurityChange change,
        string to,
        Guid userId,
        EmailLanguage language,
        DateTimeOffset now) =>
        Create(new AccountEmailRequested(NoticeKinds[change], to, userId, Token: null, EmailLanguageTag.Format(language)), now);

    public static OutboxEvent Create(AccountEmailRequested message, DateTimeOffset now)
    {
        var identity = new EventIdentity(EventType, SchemaVersion, ResourceType, message.UserId);
        var outboxEvent = OutboxEvent.Record(identity, JsonSerializer.Serialize(message, Json), now);
        outboxEvent.AddDelivery(Consumer, message.UserId.ToString("D"), now + (message.Kind == AccountEmailKind.EmailConfirmation && message.Token is { Length: 8 } code
            && code.All(char.IsAsciiDigit) ? TimeSpan.FromMinutes(10) : DeliveryWindow(message.Kind)));
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
