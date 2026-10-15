using Duora.Core.Identity;

namespace Duora.Core.Notifications;

/// <summary>
/// Monta os e-mails de conta com links para a SPA. O texto é neutro: não diz se a conta existe para quem não é o
/// titular e não repete dados além do necessário para a ação.
/// </summary>
public sealed class AccountEmails
{
    private readonly PublicOrigin _publicOrigin;

    public AccountEmails(PublicOrigin publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(publicOrigin);
        _publicOrigin = publicOrigin;
    }

    public TransactionalEmail EmailConfirmation(EmailAddress to, Guid userId, string token) =>
        new(
            to,
            "Confirme seu e-mail no Duora",
            $"""
            Olá,

            Para confirmar este endereço de e-mail no Duora, abra o link abaixo:

            {ActionLink("/confirmar-email", userId, token)}

            Se você não pediu este cadastro, ignore esta mensagem.
            """);

    public TransactionalEmail PasswordReset(EmailAddress to, Guid userId, string token) =>
        new(
            to,
            "Redefinição de senha do Duora",
            $"""
            Olá,

            Recebemos um pedido para redefinir a senha da conta ligada a este e-mail. Para escolher uma nova senha,
            abra o link abaixo:

            {ActionLink("/redefinir-senha", userId, token)}

            Se você não fez esse pedido, ignore esta mensagem; sua senha continua a mesma.
            """);

    public TransactionalEmail RegistrationAttemptOnExistingAccount(EmailAddress to) =>
        new(
            to,
            "Tentativa de cadastro no Duora",
            $"""
            Olá,

            Alguém tentou criar uma conta no Duora com este e-mail, que já tem cadastro. Se foi você, entre
            normalmente ou recupere a senha em:

            {_publicOrigin.Resolve("/recuperar-senha")}

            Se não foi você, nenhuma ação é necessária.
            """);

    private Uri ActionLink(string path, Guid userId, string token) =>
        _publicOrigin.Resolve($"{path}?userId={userId:D}&token={Uri.EscapeDataString(token)}");
}
