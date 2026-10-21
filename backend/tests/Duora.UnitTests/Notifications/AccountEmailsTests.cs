using Duora.Core;
using Duora.Core.Identity;
using Duora.Core.Notifications;

namespace Duora.UnitTests.Notifications;

public sealed class AccountEmailsTests
{
    private static readonly PublicOrigin Origin =
        PublicOrigin.TryParse("https://duora.example", out var origin) ? origin : throw new InvalidOperationException();
    private static readonly EmailAddress Support =
        EmailAddress.TryParse("suporte@duora.example", out var support) ? support : throw new InvalidOperationException();
    private static readonly Guid UserId = Guid.Parse("0199d700-7f20-7000-8000-000000000001");
    private static readonly EmailAddress Recipient = ParseEmail("ana@example.com");

    [Theory]
    [InlineData(EmailLanguage.BrazilianPortuguese)]
    [InlineData(EmailLanguage.English)]
    public void ConfirmationCodeIsShownInBodyButNeverEmbeddedInLink(EmailLanguage language)
    {
        var email = new AccountEmails(Origin, Support).EmailConfirmation(Recipient, UserId, "01234567", language);
        Assert.Contains("01234567", email.TextBody, StringComparison.Ordinal);
        var link = email.TextBody.Split((char[])['\n', ' '])
            .Single(word => word.StartsWith("https://", StringComparison.Ordinal));
        Assert.DoesNotContain("01234567", link, StringComparison.Ordinal);
        Assert.Contains("token=code", link, StringComparison.Ordinal);
        Assert.Equal("/confirm-email", new Uri(link).AbsolutePath);
    }

    [Fact]
    public void EmailConfirmationLinksToTheSpaConfirmationRoute()
    {
        var emails = new AccountEmails(Origin, Support);

        var email = emails.EmailConfirmation(Recipient, UserId, "tok-en_1", EmailLanguage.BrazilianPortuguese);

        Assert.Equal(Recipient, email.To);
        Assert.Contains(
            "https://duora.example/confirm-email#userId=0199d700-7f20-7000-8000-000000000001&token=tok-en_1",
            email.TextBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordResetLinksToTheSpaResetRoute()
    {
        var emails = new AccountEmails(Origin, Support);

        var email = emails.PasswordReset(Recipient, UserId, "tok-en_1", EmailLanguage.BrazilianPortuguese);

        Assert.Contains(
            "https://duora.example/reset-password#userId=0199d700-7f20-7000-8000-000000000001&token=tok-en_1",
            email.TextBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialsTravelInTheFragmentNeverInTheQueryString()
    {
        var emails = new AccountEmails(Origin, Support);

        var link = emails.EmailConfirmation(Recipient, UserId, "tok-en_1", EmailLanguage.BrazilianPortuguese).TextBody
            .Split((char[])['\n', ' '])
            .Single(word => word.StartsWith("https://", StringComparison.Ordinal));

        Assert.Equal("", new Uri(link).Query);
        Assert.Contains("token=tok-en_1", new Uri(link).Fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenIsEscapedInsideTheLink()
    {
        var emails = new AccountEmails(Origin, Support);

        var email = emails.PasswordReset(Recipient, UserId, "a+b/c=&x", EmailLanguage.BrazilianPortuguese);

        Assert.Contains("token=a%2Bb%2Fc%3D%26x", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingAccountNoticeCarriesNoTokenAndPointsToRecovery()
    {
        var emails = new AccountEmails(Origin, Support);

        var email = emails.RegistrationAttemptOnExistingAccount(Recipient, EmailLanguage.BrazilianPortuguese);

        Assert.DoesNotContain("token=", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://duora.example/forgot-password", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingRegistrationReminderAsksToRegisterAgainWithoutAnyLinkToConfirm()
    {
        var emails = new AccountEmails(Origin, Support);

        var email = emails.PendingRegistrationReminder(Recipient, EmailLanguage.BrazilianPortuguese);

        Assert.DoesNotContain("token=", email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("confirm-email", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://duora.example/register", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetRequestForUnknownAddressCarriesNoToken()
    {
        var emails = new AccountEmails(Origin, Support);

        var email = emails.PasswordResetForUnknownAddress(Recipient, EmailLanguage.BrazilianPortuguese);

        Assert.DoesNotContain("token=", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://duora.example/register", email.TextBody, StringComparison.Ordinal);
    }

    private static EmailAddress ParseEmail(string value) =>
        EmailAddress.TryParse(value, out var address) ? address : throw new InvalidOperationException(value);
}
