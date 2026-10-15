using Duora.Core;
using Duora.Core.Identity;
using Duora.Core.Notifications;

namespace Duora.UnitTests.Notifications;

public sealed class AccountEmailsTests
{
    private static readonly PublicOrigin Origin =
        PublicOrigin.TryParse("https://duora.example", out var origin) ? origin : throw new InvalidOperationException();
    private static readonly Guid UserId = Guid.Parse("0199d700-7f20-7000-8000-000000000001");
    private static readonly EmailAddress Recipient = ParseEmail("ana@example.com");

    [Fact]
    public void EmailConfirmationLinksToTheSpaConfirmationRoute()
    {
        var emails = new AccountEmails(Origin);

        var email = emails.EmailConfirmation(Recipient, UserId, "tok-en_1");

        Assert.Equal(Recipient, email.To);
        Assert.Contains(
            "https://duora.example/confirmar-email?userId=0199d700-7f20-7000-8000-000000000001&token=tok-en_1",
            email.TextBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordResetLinksToTheSpaResetRoute()
    {
        var emails = new AccountEmails(Origin);

        var email = emails.PasswordReset(Recipient, UserId, "tok-en_1");

        Assert.Contains(
            "https://duora.example/redefinir-senha?userId=0199d700-7f20-7000-8000-000000000001&token=tok-en_1",
            email.TextBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TokenIsEscapedInsideTheLink()
    {
        var emails = new AccountEmails(Origin);

        var email = emails.PasswordReset(Recipient, UserId, "a+b/c=&x");

        Assert.Contains("token=a%2Bb%2Fc%3D%26x", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingAccountNoticeCarriesNoTokenAndPointsToRecovery()
    {
        var emails = new AccountEmails(Origin);

        var email = emails.RegistrationAttemptOnExistingAccount(Recipient);

        Assert.DoesNotContain("token=", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://duora.example/recuperar-senha", email.TextBody, StringComparison.Ordinal);
    }

    private static EmailAddress ParseEmail(string value) =>
        EmailAddress.TryParse(value, out var address) ? address : throw new InvalidOperationException(value);
}
