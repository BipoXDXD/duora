using Duora.Core;
using Duora.Core.Identity;
using Duora.Core.Notifications;

namespace Duora.UnitTests.Notifications;

/// <summary>
/// Characterization tests: o corpo pt-BR de cada e-mail de conta, literal, igual ao da main antes da tradução
/// para en. Mudar um destes textos é decisão de produto, não efeito colateral de refatoração.
/// </summary>
public sealed class AccountEmailPortugueseTextTests
{
    private const string Token = "tok-en_1";
    private const EmailLanguage Portuguese = EmailLanguage.BrazilianPortuguese;

    private static readonly PublicOrigin Origin =
        PublicOrigin.TryParse("https://duora.example", out var origin) ? origin : throw new InvalidOperationException();
    private static readonly EmailAddress Support =
        EmailAddress.TryParse("suporte@duora.example", out var support) ? support : throw new InvalidOperationException();
    private static readonly Guid UserId = Guid.Parse("0199d700-7f20-7000-8000-000000000001");
    private static readonly EmailAddress Recipient =
        EmailAddress.TryParse("ana@example.com", out var address) ? address : throw new InvalidOperationException();

    private static readonly AccountEmails Emails = new(Origin, Support);

    [Fact]
    public void EmailConfirmationKeepsTheTextFromBeforeTheTranslation()
    {
        var email = Emails.EmailConfirmation(Recipient, UserId, Token, Portuguese);

        Assert.Equal(
            """
            Olá,

            Para confirmar este endereço de e-mail no Duora, abra o link abaixo. Vamos pedir a senha que você
            escolheu no cadastro.

            https://duora.example/confirm-email#userId=0199d700-7f20-7000-8000-000000000001&token=tok-en_1

            Se você não pediu este cadastro, ignore esta mensagem: sem a senha, ninguém confirma a conta.
            """,
            email.TextBody);
    }

    [Fact]
    public void PasswordResetKeepsTheTextFromBeforeTheTranslation()
    {
        var email = Emails.PasswordReset(Recipient, UserId, Token, Portuguese);

        Assert.Equal(
            """
            Olá,

            Recebemos um pedido para redefinir a senha da conta ligada a este e-mail. Para escolher uma nova senha,
            abra o link abaixo:

            https://duora.example/reset-password#userId=0199d700-7f20-7000-8000-000000000001&token=tok-en_1

            Se você não fez esse pedido, ignore esta mensagem; sua senha continua a mesma.
            """,
            email.TextBody);
    }

    [Fact]
    public void RegistrationAttemptOnExistingAccountKeepsTheTextFromBeforeTheTranslation()
    {
        var email = Emails.RegistrationAttemptOnExistingAccount(Recipient, Portuguese);

        Assert.Equal(
            """
            Olá,

            Alguém tentou criar uma conta no Duora com este e-mail, que já tem cadastro. Se foi você, entre
            normalmente ou recupere a senha em:

            https://duora.example/forgot-password

            Se não foi você, nenhuma ação é necessária.
            """,
            email.TextBody);
    }

    [Fact]
    public void PendingRegistrationReminderKeepsTheTextFromBeforeTheTranslation()
    {
        var email = Emails.PendingRegistrationReminder(Recipient, Portuguese);

        Assert.Equal(
            """
            Olá,

            Recebemos um pedido de redefinição de senha para este e-mail, mas o cadastro dele nunca foi
            confirmado. Para usar o Duora com este endereço, faça o cadastro de novo:

            https://duora.example/register

            Se você não fez esse pedido, ignore esta mensagem.
            """,
            email.TextBody);
    }

    [Fact]
    public void PasswordResetForUnknownAddressKeepsTheTextFromBeforeTheTranslation()
    {
        var email = Emails.PasswordResetForUnknownAddress(Recipient, Portuguese);

        Assert.Equal(
            """
            Olá,

            Recebemos um pedido de redefinição de senha para este e-mail, mas não há conta do Duora com ele.
            Se quiser criar uma, use:

            https://duora.example/register

            Se você não fez esse pedido, ignore esta mensagem.
            """,
            email.TextBody);
    }
}
