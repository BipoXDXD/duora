namespace Duora.Core.Notifications;

/// <summary>Assunto e corpo em texto simples de um e-mail, já no idioma de quem o recebe.</summary>
internal sealed record EmailText(string Subject, string Body);

/// <summary>
/// Textos dos e-mails de conta num idioma. Recebe só links montados pelo <see cref="AccountEmails"/>, com o token
/// já escapado: nenhum texto digitado por alguém entra no e-mail.
/// </summary>
internal interface IAccountEmailCatalog
{
    EmailText EmailConfirmation(Uri confirmationLink);

    EmailText PasswordReset(Uri resetLink);

    EmailText RegistrationAttemptOnExistingAccount(Uri recoveryLink);

    EmailText PendingRegistrationReminder(Uri registrationLink);

    EmailText PasswordResetForUnknownAddress(Uri registrationLink);

    EmailText PasswordResetUnavailable();
}

internal sealed class BrazilianPortugueseAccountEmails : IAccountEmailCatalog
{
    public static readonly BrazilianPortugueseAccountEmails Instance = new();

    private BrazilianPortugueseAccountEmails()
    {
    }

    public EmailText EmailConfirmation(Uri confirmationLink) =>
        new(
            "Confirme seu e-mail no Duora",
            $"""
            Olá,

            Para confirmar este endereço de e-mail no Duora, abra o link abaixo. Vamos pedir a senha que você
            escolheu no cadastro.

            {confirmationLink}

            Se você não pediu este cadastro, ignore esta mensagem: sem a senha, ninguém confirma a conta.
            """);

    public EmailText PasswordReset(Uri resetLink) =>
        new(
            "Redefinição de senha do Duora",
            $"""
            Olá,

            Recebemos um pedido para redefinir a senha da conta ligada a este e-mail. Para escolher uma nova senha,
            abra o link abaixo:

            {resetLink}

            Se você não fez esse pedido, ignore esta mensagem; sua senha continua a mesma.
            """);

    public EmailText RegistrationAttemptOnExistingAccount(Uri recoveryLink) =>
        new(
            "Tentativa de cadastro no Duora",
            $"""
            Olá,

            Alguém tentou criar uma conta no Duora com este e-mail, que já tem cadastro. Se foi você, entre
            normalmente ou recupere a senha em:

            {recoveryLink}

            Se não foi você, nenhuma ação é necessária.
            """);

    public EmailText PendingRegistrationReminder(Uri registrationLink) =>
        new(
            "Cadastro não concluído no Duora",
            $"""
            Olá,

            Recebemos um pedido de redefinição de senha para este e-mail, mas o cadastro dele nunca foi
            confirmado. Para usar o Duora com este endereço, faça o cadastro de novo:

            {registrationLink}

            Se você não fez esse pedido, ignore esta mensagem.
            """);

    public EmailText PasswordResetForUnknownAddress(Uri registrationLink) =>
        new(
            "Redefinição de senha do Duora",
            $"""
            Olá,

            Recebemos um pedido de redefinição de senha para este e-mail, mas não há conta do Duora com ele.
            Se quiser criar uma, use:

            {registrationLink}

            Se você não fez esse pedido, ignore esta mensagem.
            """);

    public EmailText PasswordResetUnavailable() =>
        new(
            "Redefinição de senha do Duora",
            """
            Olá,

            Recebemos um pedido de redefinição de senha para este e-mail. Esta conta ainda não ativou o segundo
            fator, e por isso a senha dela não pode ser redefinida por e-mail. Fale com quem administra o Duora.

            Se você não fez esse pedido, ignore esta mensagem; sua senha continua a mesma.
            """);
}

internal sealed class EnglishAccountEmails : IAccountEmailCatalog
{
    public static readonly EnglishAccountEmails Instance = new();

    private EnglishAccountEmails()
    {
    }

    public EmailText EmailConfirmation(Uri confirmationLink) =>
        new(
            "Confirm your email on Duora",
            $"""
            Hi,

            To confirm this email address on Duora, open the link below. We will ask for the password you chose
            when you signed up.

            {confirmationLink}

            If you did not sign up, ignore this message: without the password, nobody can confirm the account.
            """);

    public EmailText PasswordReset(Uri resetLink) =>
        new(
            "Duora password reset",
            $"""
            Hi,

            We received a request to reset the password of the account linked to this email. To choose a new
            password, open the link below:

            {resetLink}

            If you did not make this request, ignore this message; your password stays the same.
            """);

    public EmailText RegistrationAttemptOnExistingAccount(Uri recoveryLink) =>
        new(
            "Sign-up attempt on Duora",
            $"""
            Hi,

            Someone tried to create a Duora account with this email, which already has one. If it was you, sign
            in as usual or reset your password at:

            {recoveryLink}

            If it was not you, no action is needed.
            """);

    public EmailText PendingRegistrationReminder(Uri registrationLink) =>
        new(
            "Unfinished sign-up on Duora",
            $"""
            Hi,

            We received a password reset request for this email, but its sign-up was never confirmed. To use
            Duora with this address, sign up again:

            {registrationLink}

            If you did not make this request, ignore this message.
            """);

    public EmailText PasswordResetForUnknownAddress(Uri registrationLink) =>
        new(
            "Duora password reset",
            $"""
            Hi,

            We received a password reset request for this email, but there is no Duora account with it. If you
            want to create one, use:

            {registrationLink}

            If you did not make this request, ignore this message.
            """);

    public EmailText PasswordResetUnavailable() =>
        new(
            "Duora password reset",
            """
            Hi,

            We received a password reset request for this email. This account has not turned on its second factor
            yet, so its password cannot be reset by email. Contact whoever runs Duora.

            If you did not make this request, ignore this message; your password stays the same.
            """);
}
