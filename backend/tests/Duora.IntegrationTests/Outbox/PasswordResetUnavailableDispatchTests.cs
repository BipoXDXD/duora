using Duora.Infrastructure.Email;

namespace Duora.IntegrationTests.Outbox;

/// <summary>
/// Leitor do aviso de reset indisponível, que vai a deploy antes de qualquer gravação dele (expand/contract,
/// model.md): o worker entende o tipo, escreve o texto fixo sem link no idioma do pedido e recusa aviso com token.
/// </summary>
public sealed class PasswordResetUnavailableDispatchTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pt-BR", "Subject: Redefinição de senha do Duora\n", "não pode ser redefinida por e-mail")]
    [InlineData("en", "Subject: Duora password reset\n", "cannot be reset by email")]
    public async Task TheNoticeIsDeliveredInTheRequestLanguageWithoutAnyLink(string language, string subject, string excerpt)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.PasswordResetUnavailable, token: null, language, Ct);
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(deliveryId, Ct)).Status);
        var mail = await File.ReadAllTextAsync(Assert.Single(harness.MailFiles()), Ct);
        Assert.Contains(subject, mail, StringComparison.Ordinal);
        Assert.Contains(excerpt, mail, StringComparison.Ordinal);
        Assert.DoesNotContain(EmailOutboxHarness.PublicOrigin, mail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANoticeCarryingATokenIsRejectedWithoutSending()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.PasswordResetUnavailable, "tok-1", "pt-BR", Ct);
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal("Failed", delivery.Status);
        Assert.Equal("InvalidPayload", delivery.LastError);
        Assert.Empty(harness.MailFiles());
    }

    [Fact]
    public async Task TheNoticeExpiresAfterTwentyFourHours()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.PasswordResetUnavailable, token: null, Ct);
        harness.Time.Advance(TimeSpan.FromHours(24));
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal("Expired", (await harness.ReadDeliveryAsync(deliveryId, Ct)).Status);
        Assert.Empty(harness.MailFiles());
    }
}
