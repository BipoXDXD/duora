using Duora.Core;
using Duora.Core.Identity;
using Duora.Core.Notifications;

namespace Duora.UnitTests.Notifications;

/// <summary>
/// Avisos de mudança de credencial ao titular (TM-21): texto fixo, sem dado da requisição além do instante em UTC,
/// com link para a área de sessões, em pt-BR ou en.
/// </summary>
public sealed class SecurityNoticeEmailsTests
{
    private const string SessionsLink = "https://duora.example/conta";

    private static readonly PublicOrigin Origin =
        PublicOrigin.TryParse("https://duora.example", out var origin) ? origin : throw new InvalidOperationException();
    private static readonly EmailAddress Recipient =
        EmailAddress.TryParse("ana@example.com", out var address) ? address : throw new InvalidOperationException();

    // 14:05 em São Paulo (UTC-3) são 17:05 UTC: o e-mail mostra o instante em UTC, nunca no fuso de quem gravou.
    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 16, 14, 5, 59, TimeSpan.FromHours(-3));

    private static readonly AccountEmails Emails = new(Origin);

    public static TheoryData<SecurityChange, EmailLanguage, string, string> ExpectedTexts => new()
    {
        { SecurityChange.MfaEnabled, EmailLanguage.BrazilianPortuguese, "Verificação em duas etapas ativada no Duora", "A verificação em duas etapas foi ativada na sua conta do Duora em 2026-10-16 17:05 UTC." },
        { SecurityChange.MfaEnabled, EmailLanguage.English, "Two-step verification turned on for Duora", "Two-step verification was turned on for your Duora account on 2026-10-16 17:05 UTC." },
        { SecurityChange.MfaDisabled, EmailLanguage.BrazilianPortuguese, "Verificação em duas etapas desativada no Duora", "A verificação em duas etapas foi desativada na sua conta do Duora em 2026-10-16 17:05 UTC." },
        { SecurityChange.MfaDisabled, EmailLanguage.English, "Two-step verification turned off for Duora", "Two-step verification was turned off for your Duora account on 2026-10-16 17:05 UTC." },
        { SecurityChange.RecoveryCodesRegenerated, EmailLanguage.BrazilianPortuguese, "Novos códigos de recuperação no Duora", "Novos códigos de recuperação foram gerados para a sua conta do Duora em 2026-10-16 17:05 UTC; os anteriores deixaram de valer." },
        { SecurityChange.RecoveryCodesRegenerated, EmailLanguage.English, "New recovery codes for Duora", "New recovery codes were generated for your Duora account on 2026-10-16 17:05 UTC; the previous ones no longer work." },
        { SecurityChange.PasswordChanged, EmailLanguage.BrazilianPortuguese, "Senha alterada no Duora", "A senha da sua conta do Duora foi alterada em 2026-10-16 17:05 UTC." },
        { SecurityChange.PasswordChanged, EmailLanguage.English, "Duora password changed", "The password of your Duora account was changed on 2026-10-16 17:05 UTC." },
        { SecurityChange.PasswordReset, EmailLanguage.BrazilianPortuguese, "Senha redefinida no Duora", "A senha da sua conta do Duora foi redefinida pelo link de recuperação em 2026-10-16 17:05 UTC." },
        { SecurityChange.PasswordReset, EmailLanguage.English, "Duora password reset completed", "The password of your Duora account was reset through the recovery link on 2026-10-16 17:05 UTC." },
    };

    public static TheoryData<SecurityChange> Changes => new(Enum.GetValues<SecurityChange>());

    [Theory]
    [MemberData(nameof(ExpectedTexts))]
    public void NoticeSaysWhatChangedAndWhenInUtc(SecurityChange change, EmailLanguage language, string subject, string sentence)
    {
        var email = Emails.SecurityNotice(Recipient, change, OccurredAt, language);

        Assert.Equal(Recipient, email.To);
        Assert.Equal(subject, email.Subject);
        Assert.Contains(sentence, email.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public void NoticeLinksToTheSessionsAreaInBothLanguagesWithoutAnyCredential(SecurityChange change)
    {
        foreach (var language in Enum.GetValues<EmailLanguage>())
        {
            var email = Emails.SecurityNotice(Recipient, change, OccurredAt, language);

            Assert.Contains(SessionsLink + "\n", email.TextBody, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", email.TextBody, StringComparison.Ordinal);
            Assert.DoesNotContain("#", email.TextBody, StringComparison.Ordinal);
            Assert.DoesNotContain(Recipient.Value, email.TextBody, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public void LanguageOutsideTheAllowlistFallsBackToBrazilianPortuguese(SecurityChange change)
    {
        var unknown = Emails.SecurityNotice(Recipient, change, OccurredAt, (EmailLanguage)99);
        var portuguese = Emails.SecurityNotice(Recipient, change, OccurredAt, EmailLanguage.BrazilianPortuguese);

        Assert.Equal(portuguese.Subject, unknown.Subject);
        Assert.Equal(portuguese.TextBody, unknown.TextBody);
    }

    [Fact]
    public void ChangeOutsideTheEnumIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Emails.SecurityNotice(Recipient, (SecurityChange)99, OccurredAt, EmailLanguage.BrazilianPortuguese));
    }

    [Fact]
    public void PortugueseNoticeKeepsTheApprovedText()
    {
        var email = Emails.SecurityNotice(Recipient, SecurityChange.PasswordChanged, OccurredAt, EmailLanguage.BrazilianPortuguese);

        Assert.Equal(
            """
            Olá,

            A senha da sua conta do Duora foi alterada em 2026-10-16 17:05 UTC.

            Se foi você, nenhuma ação é necessária. Se não foi, entre no Duora, encerre as sessões que você não
            reconhece e troque a senha:

            https://duora.example/conta

            Se não conseguir entrar, use "Esqueci minha senha" na tela de entrada para recuperar o acesso.
            """,
            email.TextBody);
    }

    [Fact]
    public void EnglishNoticeKeepsTheApprovedText()
    {
        var email = Emails.SecurityNotice(Recipient, SecurityChange.MfaDisabled, OccurredAt, EmailLanguage.English);

        Assert.Equal(
            """
            Hi,

            Two-step verification was turned off for your Duora account on 2026-10-16 17:05 UTC.

            If it was you, no action is needed. If it was not, sign in to Duora, end the sessions you do not
            recognize and change your password:

            https://duora.example/conta

            If you cannot sign in, use "Forgot your password?" on the sign-in screen to recover access.
            """,
            email.TextBody);
    }
}
