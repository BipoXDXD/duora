using Duora.Infrastructure.Email;

namespace Duora.IntegrationTests.Outbox;

/// <summary>
/// O worker escreve o e-mail no idioma gravado no pedido da outbox; tag ausente (evento anterior ao campo),
/// fora da allowlist sai em en-US (ADR-0026), e <c>en</c>, gravado antes do ADR-0026, continua inglês. Tag ausente
/// ou nula só existe em pedido anterior ao campo, de quando só havia pt-BR, e sai em pt-BR.
/// Confere o assunto e o corpo do arquivo entregue.
/// </summary>
public sealed class LocalizedEmailDispatchTests(PostgresFixture postgres)
{
    private const string PortugueseSubject = "Subject: Confirme seu e-mail no Duora";
    private const string PortugueseBody = "Para confirmar este endereço de e-mail no Duora, abra o link abaixo.";
    private const string EnglishSubject = "Subject: Confirm your email on Duora";
    private const string EnglishBody = "To confirm this email address on Duora, open the link below.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("en-US", EnglishSubject, EnglishBody)]
    [InlineData("en", EnglishSubject, EnglishBody)]
    [InlineData("pt-BR", PortugueseSubject, PortugueseBody)]
    [InlineData("pt-PT", PortugueseSubject, PortugueseBody)]
    [InlineData("fr-FR", EnglishSubject, EnglishBody)]
    [InlineData("<script>", EnglishSubject, EnglishBody)]
    [InlineData(null, PortugueseSubject, PortugueseBody)]
    public async Task EmailIsDeliveredInTheLanguageOfTheRequest(string? language, string subjectLine, string bodyExcerpt)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", language, Ct);
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        var mail = await File.ReadAllTextAsync(Assert.Single(harness.MailFiles()), Ct);
        Assert.Contains(subjectLine + "\n", mail, StringComparison.Ordinal);
        Assert.Contains(bodyExcerpt, mail, StringComparison.Ordinal);
        Assert.Contains($"{EmailOutboxHarness.PublicOrigin}/confirm-email#userId=", mail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasswordResetInEnglishKeepsTheTokenInTheFragment()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        await harness.EnqueueAsync(AccountEmailKind.PasswordReset, "tok-1", "en-US", Ct);
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        var mail = await File.ReadAllTextAsync(Assert.Single(harness.MailFiles()), Ct);
        Assert.Contains("Subject: Duora password reset\n", mail, StringComparison.Ordinal);
        Assert.Contains("We received a request to reset the password", mail, StringComparison.Ordinal);
        Assert.Contains($"{EmailOutboxHarness.PublicOrigin}/reset-password#userId=", mail, StringComparison.Ordinal);
        Assert.Contains("&token=tok-1", mail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventRecordedBeforeTheLanguageFieldIsDeliveredInPortuguese()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.EmailConfirmation, "tok-1", "en-US", Ct);
        await harness.RemovePayloadFieldAsync(deliveryId, "language", Ct);
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal("Delivered", delivery.Status);
        var mail = await File.ReadAllTextAsync(Assert.Single(harness.MailFiles()), Ct);
        Assert.Contains(PortugueseSubject + "\n", mail, StringComparison.Ordinal);
        Assert.Contains(PortugueseBody, mail, StringComparison.Ordinal);
    }
}
