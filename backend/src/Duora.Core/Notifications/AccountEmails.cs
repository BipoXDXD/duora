using System.Globalization;
using Duora.Core.Identity;

namespace Duora.Core.Notifications;

/// <summary>
/// Monta os e-mails de conta com links para a SPA, no idioma pedido. O texto é neutro: não diz se a conta existe
/// para quem não é o titular e não repete dados além do necessário para a ação. Os links são os mesmos nos dois
/// idiomas: as rotas da SPA não mudam com o idioma.
/// </summary>
public sealed class AccountEmails
{
    /// <summary>Página da conta na SPA, onde fica a lista de sessões ativas (API-A09/A10).</summary>
    private const string SessionsPath = "/conta";

    private readonly PublicOrigin _publicOrigin;

    public AccountEmails(PublicOrigin publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(publicOrigin);
        _publicOrigin = publicOrigin;
    }

    public TransactionalEmail EmailConfirmation(EmailAddress to, Guid userId, string token, EmailLanguage language) =>
        Compose(to, CatalogFor(language).EmailConfirmation(ActionLink("/confirmar-email", userId, token)));

    public TransactionalEmail PasswordReset(EmailAddress to, Guid userId, string token, EmailLanguage language) =>
        Compose(to, CatalogFor(language).PasswordReset(ActionLink("/redefinir-senha", userId, token)));

    public TransactionalEmail RegistrationAttemptOnExistingAccount(EmailAddress to, EmailLanguage language) =>
        Compose(to, CatalogFor(language).RegistrationAttemptOnExistingAccount(_publicOrigin.Resolve("/recuperar-senha")));

    /// <summary>
    /// Pedido de redefinição para uma conta que nunca confirmou o e-mail. Não há link de confirmação nem de
    /// redefinição: o cadastro pendente pode ter sido feito por outra pessoa, e só um cadastro novo, feito por
    /// quem lê este e-mail, substitui o anterior (TM-66).
    /// </summary>
    public TransactionalEmail PendingRegistrationReminder(EmailAddress to, EmailLanguage language) =>
        Compose(to, CatalogFor(language).PendingRegistrationReminder(_publicOrigin.Resolve("/cadastro")));

    public TransactionalEmail PasswordResetForUnknownAddress(EmailAddress to, EmailLanguage language) =>
        Compose(to, CatalogFor(language).PasswordResetForUnknownAddress(_publicOrigin.Resolve("/cadastro")));

    /// <summary>
    /// Aviso ao titular de que uma credencial mudou (TM-21). Texto fixo: o único dado variável é o instante, em UTC e
    /// com precisão de minuto, porque o e-mail não sabe o fuso de quem lê. Não há token: o link leva à área de
    /// sessões, onde quem não reconhece a mudança encerra as sessões e troca a senha.
    /// </summary>
    public TransactionalEmail SecurityNotice(
        EmailAddress to,
        SecurityChange change,
        DateTimeOffset occurredAt,
        EmailLanguage language)
    {
        if (!Enum.IsDefined(change))
        {
            throw new ArgumentOutOfRangeException(nameof(change), change, "Mudança de credencial desconhecida.");
        }

        var occurredAtUtc = occurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        return Compose(to, CatalogFor(language).SecurityNotice(change, occurredAtUtc, _publicOrigin.Resolve(SessionsPath)));
    }

    /// <summary>Valor fora do enum (um cast de inteiro) cai no padrão, como uma tag desconhecida.</summary>
    private static IAccountEmailCatalog CatalogFor(EmailLanguage language) => language switch
    {
        EmailLanguage.English => EnglishAccountEmails.Instance,
        _ => BrazilianPortugueseAccountEmails.Instance,
    };

    private static TransactionalEmail Compose(EmailAddress to, EmailText text) => new(to, text.Subject, text.Body);

    /// <summary>
    /// Credenciais no fragmento, nunca na query: o fragmento não vai ao servidor, ao log do proxy nem ao
    /// <c>Referer</c> (auth.md, TM-19). A SPA lê o fragmento e envia userId e token no corpo do POST.
    /// </summary>
    private Uri ActionLink(string path, Guid userId, string token) =>
        _publicOrigin.Resolve($"{path}#userId={userId:D}&token={Uri.EscapeDataString(token)}");
}
