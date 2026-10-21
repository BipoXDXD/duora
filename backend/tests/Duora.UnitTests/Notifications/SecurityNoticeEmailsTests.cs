using Duora.Core;
using Duora.Core.Identity;
using Duora.Core.Notifications;

namespace Duora.UnitTests.Notifications;

/// <summary>
/// Avisos de mudança de credencial ao titular (TM-21): texto fixo, sem dado da requisição além do instante em UTC,
/// com link para a área de sessões, em pt-BR e depois em en no mesmo e-mail.
/// </summary>
public sealed class SecurityNoticeEmailsTests
{
    private const string SessionsLink = "https://duora.example/account";

    private static readonly PublicOrigin Origin =
        PublicOrigin.TryParse("https://duora.example", out var origin) ? origin : throw new InvalidOperationException();
    private static readonly EmailAddress Support =
        EmailAddress.TryParse("suporte@duora.example", out var support) ? support : throw new InvalidOperationException();
    private static readonly EmailAddress Recipient =
        EmailAddress.TryParse("ana@example.com", out var address) ? address : throw new InvalidOperationException();

    // 14:05 em São Paulo (UTC-3) são 17:05 UTC: o e-mail mostra o instante em UTC, nunca no fuso de quem gravou.
    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 16, 14, 5, 59, TimeSpan.FromHours(-3));

    private static readonly AccountEmails Emails = new(Origin, Support);

    public static TheoryData<SecurityChange, string, string, string> ExpectedTexts => new()
    {
        {
            SecurityChange.RecoveryCodeUsed,
            "Código de recuperação usado no Duora / Recovery code used for Duora",
            "Um código de recuperação foi usado para acessar sua conta do Duora em 2026-10-16 17:05 UTC.",
            "A recovery code was used to access your Duora account on 2026-10-16 17:05 UTC."
        },
        {
            SecurityChange.MfaEnabled,
            "Verificação em duas etapas ativada no Duora / Two-step verification turned on for Duora",
            "A verificação em duas etapas foi ativada na sua conta do Duora em 2026-10-16 17:05 UTC.",
            "Two-step verification was turned on for your Duora account on 2026-10-16 17:05 UTC."
        },
        {
            SecurityChange.MfaDisabled,
            "Verificação em duas etapas desativada no Duora / Two-step verification turned off for Duora",
            "A verificação em duas etapas foi desativada na sua conta do Duora em 2026-10-16 17:05 UTC.",
            "Two-step verification was turned off for your Duora account on 2026-10-16 17:05 UTC."
        },
        {
            SecurityChange.RecoveryCodesRegenerated,
            "Novos códigos de recuperação no Duora / New recovery codes for Duora",
            "Novos códigos de recuperação foram gerados para a sua conta do Duora em 2026-10-16 17:05 UTC; os anteriores deixaram de valer.",
            "New recovery codes were generated for your Duora account on 2026-10-16 17:05 UTC; the previous ones no longer work."
        },
        {
            SecurityChange.PasswordChanged,
            "Senha alterada no Duora / Duora password changed",
            "A senha da sua conta do Duora foi alterada em 2026-10-16 17:05 UTC.",
            "The password of your Duora account was changed on 2026-10-16 17:05 UTC."
        },
        {
            SecurityChange.PasswordReset,
            "Senha redefinida no Duora / Duora password reset completed",
            "A senha da sua conta do Duora foi redefinida em 2026-10-16 17:05 UTC, pelo link de recuperação ou por quem administra o Duora.",
            "The password of your Duora account was reset on 2026-10-16 17:05 UTC, through the recovery link or by whoever runs Duora."
        },
    };

    public static TheoryData<SecurityChange> Changes => new(Enum.GetValues<SecurityChange>());

    [Theory]
    [MemberData(nameof(ExpectedTexts))]
    public void NoticeSaysWhatChangedAndWhenInUtcInPortugueseThenEnglish(
        SecurityChange change,
        string subject,
        string portuguese,
        string english)
    {
        var email = Emails.SecurityNotice(Recipient, change, OccurredAt);

        Assert.Equal(Recipient, email.To);
        Assert.Equal(subject, email.Subject);
        var portugueseAt = email.TextBody.IndexOf(portuguese, StringComparison.Ordinal);
        var englishAt = email.TextBody.IndexOf(english, StringComparison.Ordinal);
        Assert.True(portugueseAt >= 0, "Falta a frase em pt-BR.");
        Assert.True(englishAt > portugueseAt, "A versão em en deve vir depois da pt-BR.");
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public void NoticeLinksToTheSessionsAreaInBothVersionsWithoutAnyCredential(SecurityChange change)
    {
        var email = Emails.SecurityNotice(Recipient, change, OccurredAt);

        Assert.Equal(2, CountOccurrences(email.TextBody, SessionsLink + "\n"));
        Assert.DoesNotContain("token=", email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("#", email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Recipient.Value, email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MfaEnabledNoticeSendsToSupportInsteadOfThePasswordReset()
    {
        // O reset não remove o MFA (TM-22): quem perdeu a conta para um atacante que ativou o MFA precisa do suporte.
        var email = Emails.SecurityNotice(Recipient, SecurityChange.MfaEnabled, OccurredAt);

        Assert.DoesNotContain("Esqueci minha senha", email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Forgot your password?", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("fale com o suporte em suporte@duora.example.", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("contact support at suporte@duora.example.", email.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SecurityChange.MfaDisabled)]
    [InlineData(SecurityChange.RecoveryCodesRegenerated)]
    [InlineData(SecurityChange.PasswordChanged)]
    [InlineData(SecurityChange.PasswordReset)]
    public void OtherNoticesPointToThePasswordReset(SecurityChange change)
    {
        var email = Emails.SecurityNotice(Recipient, change, OccurredAt);

        Assert.Contains("use \"Esqueci minha senha\" na tela de entrada", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("use \"Forgot your password?\" on the sign-in screen", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeOutsideTheEnumIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Emails.SecurityNotice(Recipient, (SecurityChange)99, OccurredAt));
    }

    [Fact]
    public void PasswordChangedNoticeKeepsTheApprovedText()
    {
        var email = Emails.SecurityNotice(Recipient, SecurityChange.PasswordChanged, OccurredAt);

        Assert.Equal(
            """
            Olá,

            A senha da sua conta do Duora foi alterada em 2026-10-16 17:05 UTC.

            Se foi você, nenhuma ação é necessária. Se não foi, entre no Duora, encerre as sessões que você não
            reconhece e troque a senha:

            https://duora.example/account

            Se não conseguir entrar, use "Esqueci minha senha" na tela de entrada para recuperar o acesso.

            ---

            Hi,

            The password of your Duora account was changed on 2026-10-16 17:05 UTC.

            If it was you, no action is needed. If it was not, sign in to Duora, end the sessions you do not
            recognize and change your password:

            https://duora.example/account

            If you cannot sign in, use "Forgot your password?" on the sign-in screen to recover access.
            """,
            email.TextBody);
    }

    [Fact]
    public void MfaEnabledNoticeKeepsTheApprovedText()
    {
        var email = Emails.SecurityNotice(Recipient, SecurityChange.MfaEnabled, OccurredAt);

        Assert.Equal(
            """
            Olá,

            A verificação em duas etapas foi ativada na sua conta do Duora em 2026-10-16 17:05 UTC.

            Se foi você, nenhuma ação é necessária. Se não foi, entre no Duora, encerre as sessões que você não
            reconhece e troque a senha:

            https://duora.example/account

            Se não conseguir entrar, a recuperação de senha não basta: ela não remove a verificação em duas etapas.
            Responda a este e-mail ou fale com o suporte em suporte@duora.example.

            ---

            Hi,

            Two-step verification was turned on for your Duora account on 2026-10-16 17:05 UTC.

            If it was you, no action is needed. If it was not, sign in to Duora, end the sessions you do not
            recognize and change your password:

            https://duora.example/account

            If you cannot sign in, a password reset is not enough: it does not remove two-step verification.
            Reply to this email or contact support at suporte@duora.example.
            """,
            email.TextBody);
    }

    [Theory]
    [InlineData("America/New_York", "2026-03-08T06:59:00Z", "08/03/2026, 01:59", "3/8/26, 1:59 AM", "UTC−05:00")]
    [InlineData("America/New_York", "2026-03-08T07:00:00Z", "08/03/2026, 03:00", "3/8/26, 3:00 AM", "UTC−04:00")]
    [InlineData("America/New_York", "2026-11-01T05:30:00Z", "01/11/2026, 01:30", "11/1/26, 1:30 AM", "UTC−04:00")]
    [InlineData("America/New_York", "2026-11-01T06:30:00Z", "01/11/2026, 01:30", "11/1/26, 1:30 AM", "UTC−05:00")]
    [InlineData("America/Sao_Paulo", "2026-03-08T06:59:00Z", "08/03/2026, 03:59", "3/8/26, 3:59 AM", "UTC−03:00")]
    [InlineData("America/Sao_Paulo", "2026-11-01T06:30:00Z", "01/11/2026, 03:30", "11/1/26, 3:30 AM", "UTC−03:00")]
    public void PreferredZoneAddsBothLocalLiteralsWithoutRemovingTheUtcReference(string zone, string value, string pt, string en, string offset)
    {
        var instant = DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var email = Emails.SecurityNotice(Recipient, SecurityChange.PasswordChanged, instant, zone);
        Assert.Contains($"No seu fuso salvo: {pt} — {zone} · {offset}", email.TextBody, StringComparison.Ordinal);
        Assert.Contains($"In your saved time zone: {en} — {zone} · {offset}", email.TextBody, StringComparison.Ordinal);
        var original = Emails.SecurityNotice(Recipient, SecurityChange.PasswordChanged, instant);
        var bodies = original.TextBody.Split("\n\n---\n\n", StringSplitOptions.None);
        Assert.All(bodies, body => Assert.Contains(body, email.TextBody, StringComparison.Ordinal));
        Assert.DoesNotContain("token=", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedUtcKeepsTheExactUtcOnlyMessageAndInvalidZoneIsRejected()
    {
        Assert.Equal(Emails.SecurityNotice(Recipient, SecurityChange.PasswordChanged, OccurredAt).TextBody,
            Emails.SecurityNotice(Recipient, SecurityChange.PasswordChanged, OccurredAt, "UTC").TextBody);
        Assert.Throws<ArgumentException>(() => Emails.SecurityNotice(Recipient, SecurityChange.PasswordChanged, OccurredAt, "Eastern Standard Time"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
