using System.Text;
using Duora.Infrastructure.Identity;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>Solicitação já aberta no fornecedor simulado, pronta para receber callback.</summary>
internal sealed record OpenVerification(TestAccount Account, Guid VerificationId, string Reference, string Nonce);

/// <summary>
/// Callbacks do fornecedor simulado pela rota real <c>/webhooks/fake</c>, sem cookie nem antiforgery, como o
/// fornecedor os mandaria. A assinatura usa o mesmo protocolo do adaptador (<see cref="FakeAgeCallbackProtocol"/>).
/// </summary>
internal sealed class AgeWebhookScenario(PostgresFixture postgres, DuoraWebFactory factory, TimeProvider time)
{
    public const string WebhookPath = "/webhooks/fake";

    public AgeVerificationScenario Verification => new(postgres, factory);

    /// <summary>Conta pendente com solicitação aberta pela API e referência gravada (o passo do worker).</summary>
    public async Task<OpenVerification> OpenVerificationAsync()
    {
        var (account, browser) = await Verification.SignedInPendingAccountAsync();
        using (browser)
        using (await AgeVerificationScenario.RequestAsync(browser))
        {
        }

        var row = Assert.Single(await Verification.VerificationsAsync(account.UserId));
        var reference = FakeAgeEligibilityProvider.ReferenceFor(row.Id);
        await Verification.MarkProviderFlowReadyAsync(row.Id, $"{AgeVerificationScenario.FakeFlowOrigin}/flow/{reference}");
        return new OpenVerification(account, row.Id, reference, row.RequestNonce);
    }

    public static byte[] Body(OpenVerification open, string outcome, string? eventId = null) =>
        FakeAgeCallbackProtocol.Body(eventId ?? $"evt_{Guid.NewGuid():N}", open.Reference, open.Nonce, outcome);

    public string Sign(byte[] body, string? secret = null, TimeSpan? skew = null) =>
        FakeAgeCallbackProtocol.Sign(
            body,
            (time.GetUtcNow() + (skew ?? TimeSpan.Zero)).ToUnixTimeSeconds(),
            secret ?? AgeVerificationScenario.CurrentSigningSecret);

    public Task<HttpResponseMessage> SendAsync(byte[] body, string? signature, string path = WebhookPath) =>
        SendAsync(body, signature is null ? [] : [signature], path);

    /// <summary>Cada item de <paramref name="signatures"/> vira um header próprio (para testar header repetido).</summary>
    public async Task<HttpResponseMessage> SendAsync(
        byte[] body,
        IReadOnlyList<string> signatures,
        string path = WebhookPath,
        string? contentType = "application/json")
    {
        using var client = factory.CreateClient(new() { HandleCookies = false, BaseAddress = new Uri(DuoraWebFactory.PublicOrigin) });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new ByteArrayContent(body),
        };
        if (contentType is not null)
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        foreach (var signature in signatures)
        {
            request.Headers.TryAddWithoutValidation(FakeAgeCallbackProtocol.SignatureHeader, signature);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public Task<HttpResponseMessage> SendSignedAsync(byte[] body) => SendAsync(body, Sign(body));

    public Task<long> CountReceiptsAsync(string? eventId = null) =>
        Verification.Identity.ScalarAsync(
            "select count(*) from ops.webhook_receipts where provider = 'fake' and ($1 = '' or provider_event_id = $1)",
            eventId ?? "");

    public Task<long> CountReceiptsForReferenceAsync(string reference) =>
        Verification.Identity.ScalarAsync(
            "select count(*) from ops.webhook_receipts where provider = 'fake' and payload->>'reference' = $1",
            reference);

    public Task<string> ReceiptStateAsync(string eventId) =>
        Verification.Identity.ScalarTextAsync(
            "select state from ops.webhook_receipts where provider = 'fake' and provider_event_id = $1",
            eventId);

    /// <summary>
    /// Barreira no banco: trava a conta numa transação do teste, para os callbacks chegarem juntos e esperarem no
    /// lock (ver <see cref="WaitForLockWaitersAsync"/>).
    /// </summary>
    public static async Task<NpgsqlTransaction> HoldAccountLockAsync(NpgsqlConnection connection, Guid userId)
    {
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select 1 from identity.users where id = $1 for update", connection, transaction);
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return transaction;
    }

    public async Task WaitForLockWaitersAsync(int waiters)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            var count = await Verification.Identity.ScalarAsync(
                "select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'");
            if (count >= waiters)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Só {count} de {waiters} conexões chegaram à barreira.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
    }

    public static string Text(byte[] body) => Encoding.UTF8.GetString(body);
}
