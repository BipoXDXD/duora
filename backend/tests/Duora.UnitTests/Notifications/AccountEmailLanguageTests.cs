using Duora.Core;
using Duora.Core.Identity;
using Duora.Core.Notifications;

namespace Duora.UnitTests.Notifications;

public sealed class AccountEmailLanguageTests
{
    private const string Token = "tok-en_1";

    private static readonly PublicOrigin Origin =
        PublicOrigin.TryParse("https://duora.example", out var origin) ? origin : throw new InvalidOperationException();
    private static readonly EmailAddress Support =
        EmailAddress.TryParse("suporte@duora.example", out var support) ? support : throw new InvalidOperationException();
    private static readonly Guid UserId = Guid.Parse("0199d700-7f20-7000-8000-000000000001");
    private static readonly EmailAddress Recipient =
        EmailAddress.TryParse("ana@example.com", out var address) ? address : throw new InvalidOperationException();

    private static readonly AccountEmails Emails = new(Origin, Support);

    public static TheoryData<string, EmailLanguage, string, string> ExpectedTexts => new()
    {
        { "EmailConfirmation", EmailLanguage.BrazilianPortuguese, "Confirme seu e-mail no Duora", "Para confirmar este endereço de e-mail no Duora" },
        { "EmailConfirmation", EmailLanguage.English, "Confirm your email on Duora", "To confirm this email address on Duora" },
        { "PasswordReset", EmailLanguage.BrazilianPortuguese, "Redefinição de senha do Duora", "Recebemos um pedido para redefinir a senha" },
        { "PasswordReset", EmailLanguage.English, "Duora password reset", "We received a request to reset the password" },
        { "RegistrationAttemptOnExistingAccount", EmailLanguage.BrazilianPortuguese, "Tentativa de cadastro no Duora", "Alguém tentou criar uma conta no Duora" },
        { "RegistrationAttemptOnExistingAccount", EmailLanguage.English, "Sign-up attempt on Duora", "Someone tried to create a Duora account" },
        { "PendingRegistrationReminder", EmailLanguage.BrazilianPortuguese, "Cadastro não concluído no Duora", "o cadastro dele nunca foi" },
        { "PendingRegistrationReminder", EmailLanguage.English, "Unfinished sign-up on Duora", "its sign-up was never confirmed" },
        { "PasswordResetForUnknownAddress", EmailLanguage.BrazilianPortuguese, "Redefinição de senha do Duora", "não há conta do Duora com ele" },
        { "PasswordResetForUnknownAddress", EmailLanguage.English, "Duora password reset", "there is no Duora account with it" },
        { "PasswordResetUnavailable", EmailLanguage.BrazilianPortuguese, "Redefinição de senha do Duora", "não pode ser redefinida por e-mail" },
        { "PasswordResetUnavailable", EmailLanguage.English, "Duora password reset", "cannot be reset by email" },
    };

    [Theory]
    [MemberData(nameof(ExpectedTexts))]
    public void EmailIsWrittenInTheRequestedLanguage(string kind, EmailLanguage language, string subject, string bodyExcerpt)
    {
        var email = Compose(kind, language);

        Assert.Equal(subject, email.Subject);
        Assert.Contains(bodyExcerpt, email.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EmailConfirmation", "https://duora.example/confirmar-email#userId=0199d700-7f20-7000-8000-000000000001&token=tok-en_1")]
    [InlineData("PasswordReset", "https://duora.example/redefinir-senha#userId=0199d700-7f20-7000-8000-000000000001&token=tok-en_1")]
    [InlineData("RegistrationAttemptOnExistingAccount", "https://duora.example/recuperar-senha")]
    [InlineData("PendingRegistrationReminder", "https://duora.example/cadastro")]
    [InlineData("PasswordResetForUnknownAddress", "https://duora.example/cadastro")]
    public void EnglishEmailKeepsTheSameLinkAsThePortugueseOne(string kind, string link)
    {
        var english = Compose(kind, EmailLanguage.English);
        var portuguese = Compose(kind, EmailLanguage.BrazilianPortuguese);

        Assert.Contains(link, english.TextBody, StringComparison.Ordinal);
        Assert.Contains(link, portuguese.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EmailConfirmation")]
    [InlineData("PasswordReset")]
    [InlineData("RegistrationAttemptOnExistingAccount")]
    [InlineData("PendingRegistrationReminder")]
    [InlineData("PasswordResetForUnknownAddress")]
    [InlineData("PasswordResetUnavailable")]
    public void LanguageOutsideTheAllowlistFallsBackToBrazilianPortuguese(string kind)
    {
        var unknown = Compose(kind, (EmailLanguage)99);
        var portuguese = Compose(kind, EmailLanguage.BrazilianPortuguese);

        Assert.Equal(portuguese.Subject, unknown.Subject);
        Assert.Equal(portuguese.TextBody, unknown.TextBody);
    }

    [Theory]
    [InlineData(EmailLanguage.BrazilianPortuguese)]
    [InlineData(EmailLanguage.English)]
    public void PasswordResetUnavailableCarriesNoLink(EmailLanguage language)
    {
        var email = Compose("PasswordResetUnavailable", language);

        Assert.DoesNotContain("https://", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void EnglishTokenIsEscapedInsideTheFragment()
    {
        var email = Emails.PasswordReset(Recipient, UserId, "a+b/c=&x", EmailLanguage.English);

        Assert.Contains("#userId=0199d700-7f20-7000-8000-000000000001&token=a%2Bb%2Fc%3D%26x", email.TextBody, StringComparison.Ordinal);
    }

    private static TransactionalEmail Compose(string kind, EmailLanguage language) => kind switch
    {
        "EmailConfirmation" => Emails.EmailConfirmation(Recipient, UserId, Token, language),
        "PasswordReset" => Emails.PasswordReset(Recipient, UserId, Token, language),
        "RegistrationAttemptOnExistingAccount" => Emails.RegistrationAttemptOnExistingAccount(Recipient, language),
        "PendingRegistrationReminder" => Emails.PendingRegistrationReminder(Recipient, language),
        "PasswordResetForUnknownAddress" => Emails.PasswordResetForUnknownAddress(Recipient, language),
        "PasswordResetUnavailable" => Emails.PasswordResetUnavailable(Recipient, language),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Tipo de e-mail sem caso de teste."),
    };
}
