using Duora.Core;
using Duora.Core.Identity;
using Duora.Core.Notifications;

namespace Duora.UnitTests.Notifications;

/// <summary>
/// Aviso de promoção da lista de espera (issue #320, ADR-0056): bilíngue, remete ao estado atual e não carrega nome do
/// evento, horário, identificador nem o destinatário no corpo.
/// </summary>
public sealed class WaitlistPromotionNoticeEmailTests
{
    private static readonly PublicOrigin Origin =
        PublicOrigin.TryParse("https://duora.example", out var origin) ? origin : throw new InvalidOperationException();
    private static readonly EmailAddress Support =
        EmailAddress.TryParse("suporte@duora.example", out var support) ? support : throw new InvalidOperationException();
    private static readonly EmailAddress Recipient =
        EmailAddress.TryParse("ana@example.com", out var to) ? to : throw new InvalidOperationException();

    [Fact]
    public void TheNoticeIsBilingualAndPointsToTheCurrentStateOfTheEvents()
    {
        var email = new AccountEmails(Origin, Support).WaitlistPromotionNotice(Recipient);

        Assert.Equal(Recipient, email.To);
        Assert.Equal("Você saiu da lista de espera no Duora / You are off the waitlist on Duora", email.Subject);
        Assert.Contains("lista de espera", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("waitlist", email.TextBody, StringComparison.Ordinal);
        Assert.Equal(2, email.TextBody.Split("https://duora.example/events").Length - 1);
    }

    [Fact]
    public void TheNoticeDoesNotPromiseASeatThatMayHaveChanged()
    {
        var body = new AccountEmails(Origin, Support).WaitlistPromotionNotice(Recipient).TextBody;

        Assert.Contains("estado atual", body, StringComparison.Ordinal);
        Assert.Contains("current status", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@example.com", body, StringComparison.Ordinal);
        Assert.DoesNotContain("#", body, StringComparison.Ordinal);
    }
}
