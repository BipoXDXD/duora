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

    /// <summary>
    /// Aviso de mudança de credencial. <paramref name="occurredAtUtc"/> já vem formatado em UTC; é o único dado
    /// variável do texto, além do link fixo para as sessões.
    /// </summary>
    EmailText SecurityNotice(SecurityChange change, string occurredAtUtc, Uri sessionsLink);
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

    public EmailText SecurityNotice(SecurityChange change, string occurredAtUtc, Uri sessionsLink)
    {
        var (subject, sentence) = change switch
        {
            SecurityChange.MfaEnabled => (
                "Verificação em duas etapas ativada no Duora",
                $"A verificação em duas etapas foi ativada na sua conta do Duora em {occurredAtUtc}."),
            SecurityChange.MfaDisabled => (
                "Verificação em duas etapas desativada no Duora",
                $"A verificação em duas etapas foi desativada na sua conta do Duora em {occurredAtUtc}."),
            SecurityChange.RecoveryCodesRegenerated => (
                "Novos códigos de recuperação no Duora",
                $"Novos códigos de recuperação foram gerados para a sua conta do Duora em {occurredAtUtc}; os anteriores deixaram de valer."),
            SecurityChange.PasswordChanged => (
                "Senha alterada no Duora",
                $"A senha da sua conta do Duora foi alterada em {occurredAtUtc}."),
            SecurityChange.PasswordReset => (
                "Senha redefinida no Duora",
                $"A senha da sua conta do Duora foi redefinida pelo link de recuperação em {occurredAtUtc}."),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Mudança de credencial desconhecida."),
        };
        return new(
            subject,
            $"""
            Olá,

            {sentence}

            Se foi você, nenhuma ação é necessária. Se não foi, entre no Duora, encerre as sessões que você não
            reconhece e troque a senha:

            {sessionsLink}

            Se não conseguir entrar, use "Esqueci minha senha" na tela de entrada para recuperar o acesso.
            """);
    }
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

    public EmailText SecurityNotice(SecurityChange change, string occurredAtUtc, Uri sessionsLink)
    {
        var (subject, sentence) = change switch
        {
            SecurityChange.MfaEnabled => (
                "Two-step verification turned on for Duora",
                $"Two-step verification was turned on for your Duora account on {occurredAtUtc}."),
            SecurityChange.MfaDisabled => (
                "Two-step verification turned off for Duora",
                $"Two-step verification was turned off for your Duora account on {occurredAtUtc}."),
            SecurityChange.RecoveryCodesRegenerated => (
                "New recovery codes for Duora",
                $"New recovery codes were generated for your Duora account on {occurredAtUtc}; the previous ones no longer work."),
            SecurityChange.PasswordChanged => (
                "Duora password changed",
                $"The password of your Duora account was changed on {occurredAtUtc}."),
            SecurityChange.PasswordReset => (
                "Duora password reset completed",
                $"The password of your Duora account was reset through the recovery link on {occurredAtUtc}."),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Mudança de credencial desconhecida."),
        };
        return new(
            subject,
            $"""
            Hi,

            {sentence}

            If it was you, no action is needed. If it was not, sign in to Duora, end the sessions you do not
            recognize and change your password:

            {sessionsLink}

            If you cannot sign in, use "Forgot your password?" on the sign-in screen to recover access.
            """);
    }
}
