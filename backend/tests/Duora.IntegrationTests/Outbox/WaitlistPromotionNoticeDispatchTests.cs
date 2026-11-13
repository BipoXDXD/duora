using Duora.Infrastructure.Email;

namespace Duora.IntegrationTests.Outbox;

/// <summary>
/// Leitor do aviso de promoção (issue #320, ADR-0056) no consumidor de e-mail já existente: sai bilíngue, sem token, e
/// pedido com token é defeito de quem gravou.
/// </summary>
public sealed class WaitlistPromotionNoticeDispatchTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheNoticeIsDeliveredInBothLanguagesWithALinkToTheEvents()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.WaitlistPromotionNotice, token: null, language: null, Ct);
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(deliveryId, Ct)).Status);
        var mail = await File.ReadAllTextAsync(Assert.Single(harness.MailFiles()), Ct);
        Assert.Contains("lista de espera", mail, StringComparison.Ordinal);
        Assert.Contains("waitlist", mail, StringComparison.Ordinal);
        Assert.Contains($"{EmailOutboxHarness.PublicOrigin}/events", mail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANoticeCarryingATokenIsRejectedWithoutSending()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.WaitlistPromotionNotice, "tok-1", "pt-BR", Ct);
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal("Failed", delivery.Status);
        Assert.Empty(harness.MailFiles());
    }
}
