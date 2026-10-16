using System.Net;
using System.Text;
using Duora.Infrastructure.Identity;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>
/// Webhook do fornecedor (spec 8.5 e 16.4; ADR-0024, decisão 2; TM-36, TM-37, T-17): assinatura HMAC sobre os
/// bytes crus, janela de ±5 minutos, recibo único gravado antes do efeito e transição condicional, numa transação.
/// </summary>
public sealed class AgeWebhookTests : IAsyncDisposable
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(365);

    private readonly PostgresFixture _postgres;
    // Segundo inteiro: o header leva segundos Unix, e a borda da janela (±300 s) fica exata.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;

    public AgeWebhookTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = _time,
            Settings = AgeVerificationScenario.FakeProviderSettings(),
        };
    }

    private AgeWebhookScenario Scenario => new(_postgres, _factory, _time);

    private AgeVerificationScenario Verification => Scenario.Verification;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    public static TheoryData<string> InvalidSignatures => new()
    {
        "missing",
        "wrong-secret",
        "tampered-body",
        "no-timestamp",
        "no-v1",
        "short-v1",
        "not-hex",
        "garbage",
    };

    [Theory]
    [MemberData(nameof(InvalidSignatures))]
    [Trait("Threat", "TM-36")]
    public async Task WebhookWithInvalidSignatureIsRejectedWithoutSideEffects(string kind)
    {
        var open = await Scenario.OpenVerificationAsync();
        var eventId = $"evt_{Guid.NewGuid():N}";
        var body = AgeWebhookScenario.Body(open, "verified_adult", eventId);
        var valid = Scenario.Sign(body);
        var timestamp = _time.GetUtcNow().ToUnixTimeSeconds();
        var (sentBody, signature) = kind switch
        {
            "missing" => (body, (string?)null),
            "wrong-secret" => (body, Scenario.Sign(body, secret: new string('0', 64))),
            "tampered-body" => (AgeWebhookScenario.Body(open, "verified_adult", eventId + "x"), valid),
            "no-timestamp" => (body, valid[valid.IndexOf("v1=", StringComparison.Ordinal)..]),
            "no-v1" => (body, $"t={timestamp}"),
            "short-v1" => (body, valid[..^2]),
            "not-hex" => (body, $"t={timestamp},v1={new string('z', 64)}"),
            _ => (body, "isto não é uma assinatura"),
        };

        using var response = await Scenario.SendAsync(sentBody, signature);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "WEBHOOK_SIGNATURE_INVALID");
        await AssertNothingChangedAsync(open, eventId);
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    [InlineData(-3600)]
    [Trait("Threat", "TM-36")]
    public async Task WebhookReplayOutsideTimestampWindowIsRejected(int skewSeconds)
    {
        var open = await Scenario.OpenVerificationAsync();
        var eventId = $"evt_{Guid.NewGuid():N}";
        var body = AgeWebhookScenario.Body(open, "verified_adult", eventId);

        using var response = await Scenario.SendAsync(body, Scenario.Sign(body, skew: TimeSpan.FromSeconds(skewSeconds)));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "WEBHOOK_SIGNATURE_INVALID");
        await AssertNothingChangedAsync(open, eventId);
    }

    [Theory]
    [InlineData(-300)]
    [InlineData(300)]
    public async Task WebhookOnTheEdgeOfTheTimestampWindowIsAccepted(int skewSeconds)
    {
        var open = await Scenario.OpenVerificationAsync();
        var body = AgeWebhookScenario.Body(open, "verified_adult");

        using var response = await Scenario.SendAsync(body, Scenario.Sign(body, skew: TimeSpan.FromSeconds(skewSeconds)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Active", await Verification.AccountStatusAsync(open.Account.UserId));
    }

    [Fact]
    [Trait("Threat", "TM-37")]
    public async Task AgeVerificationResultRecordsPolicyVersionAndExpiry()
    {
        var open = await Scenario.OpenVerificationAsync();
        var eventId = $"evt_{Guid.NewGuid():N}";
        var decidedAt = _time.GetUtcNow();

        using var response = await Scenario.SendSignedAsync(AgeWebhookScenario.Body(open, "verified_adult", eventId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var verification = Assert.Single(await Verification.VerificationsAsync(open.Account.UserId));
        Assert.Equal("VerifiedAdult", verification.State);
        Assert.Equal("test-2026-10", verification.PolicyVersion);
        Assert.Equal("fake", verification.Provider);
        Assert.Equal(open.Reference, verification.ProviderReference);
        Assert.Equal(decidedAt, verification.DecidedAt);
        Assert.Equal(decidedAt + Validity, verification.ExpiresAt);
        Assert.Equal("Callback", verification.DecisionSource);
        Assert.Equal(2, verification.Version);
        Assert.Equal("Active", await Verification.AccountStatusAsync(open.Account.UserId));
        Assert.Equal("Processed", await Scenario.ReceiptStateAsync(eventId));
        Assert.Equal(1, await Verification.CountAuditAsync("identity.age_verification_decided", open.VerificationId));
        Assert.Equal(1, await Verification.CountAuditAsync("identity.account_activated", open.Account.UserId));
    }

    [Fact]
    public async Task NotAdultRestrictsTheAccountAndIsAudited()
    {
        var open = await Scenario.OpenVerificationAsync();

        using var response = await Scenario.SendSignedAsync(AgeWebhookScenario.Body(open, "not_adult"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var verification = Assert.Single(await Verification.VerificationsAsync(open.Account.UserId));
        Assert.Equal("Rejected", verification.State);
        Assert.Null(verification.ExpiresAt);
        Assert.Equal("Restricted", await Verification.AccountStatusAsync(open.Account.UserId));
        Assert.Equal(1, await Verification.CountAuditAsync("identity.account_restricted", open.Account.UserId));
        Assert.Equal(0, await Verification.CountAuditAsync("identity.account_activated", open.Account.UserId));
    }

    [Fact]
    public async Task InconclusiveGoesToManualReviewAndTheAccountStaysPending()
    {
        var open = await Scenario.OpenVerificationAsync();

        using var response = await Scenario.SendSignedAsync(AgeWebhookScenario.Body(open, "inconclusive"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("ManualReview", Assert.Single(await Verification.VerificationsAsync(open.Account.UserId)).State);
        Assert.Equal("PendingEligibility", await Verification.AccountStatusAsync(open.Account.UserId));
        Assert.Equal(1, await Verification.CountAuditAsync("identity.age_verification_decided", open.VerificationId));
    }

    [Fact]
    public async Task CallbackSignedWithTheSecondSecretIsAccepted()
    {
        var open = await Scenario.OpenVerificationAsync();
        var body = AgeWebhookScenario.Body(open, "verified_adult");

        using var response = await Scenario.SendAsync(body, Scenario.Sign(body, AgeVerificationScenario.NextSigningSecret));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Active", await Verification.AccountStatusAsync(open.Account.UserId));
    }

    [Fact]
    [Trait("Acceptance", "T-17")]
    public async Task DuplicateCallbackHasASingleEffect()
    {
        var open = await Scenario.OpenVerificationAsync();
        var eventId = $"evt_{Guid.NewGuid():N}";
        var body = AgeWebhookScenario.Body(open, "verified_adult", eventId);
        var simulator = new FakeAgeProviderSimulator(FakeAgeProviderMode.DuplicateCallback);
        var deliveries = simulator.Deliveries(body, Scenario.Sign(body));

        var statuses = new List<HttpStatusCode>();
        foreach (var (sentBody, signature) in deliveries)
        {
            using var response = await Scenario.SendAsync(sentBody, signature);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NoContent], statuses);
        Assert.Equal(1, await Scenario.CountReceiptsAsync(eventId));
        Assert.Equal(1, await Verification.CountAuditAsync("identity.age_verification_decided", open.VerificationId));
        Assert.Equal(1, await Verification.CountAuditAsync("identity.account_activated", open.Account.UserId));
        Assert.Equal(2, Assert.Single(await Verification.VerificationsAsync(open.Account.UserId)).Version);
    }

    [Fact]
    [Trait("Acceptance", "T-17")]
    public async Task TwoConcurrentCallbacksResultInASingleTransition()
    {
        var open = await Scenario.OpenVerificationAsync();
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        Task<HttpResponseMessage>[] callbacks;
        await using (var barrier = await AgeWebhookScenario.HoldAccountLockAsync(connection, open.Account.UserId))
        {
            callbacks =
            [
                Scenario.SendSignedAsync(AgeWebhookScenario.Body(open, "verified_adult")),
                Scenario.SendSignedAsync(AgeWebhookScenario.Body(open, "not_adult")),
            ];
            await Scenario.WaitForLockWaitersAsync(2);
            await barrier.CommitAsync(TestContext.Current.CancellationToken);
        }

        var responses = await Task.WhenAll(callbacks);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        var verification = Assert.Single(await Verification.VerificationsAsync(open.Account.UserId));
        var account = await Verification.AccountStatusAsync(open.Account.UserId);
        Assert.Equal(2, verification.Version);
        Assert.Equal(1, await Verification.CountAuditAsync("identity.age_verification_decided", open.VerificationId));
        Assert.True(
            (verification.State, account) is ("VerifiedAdult", "Active") or ("Rejected", "Restricted"),
            $"Estado incoerente: {verification.State} com conta {account}.");
        Assert.Equal(2, await Scenario.CountReceiptsForReferenceAsync(open.Reference));
    }

    [Fact]
    [Trait("Acceptance", "T-17")]
    public async Task TheSameCallbackTwiceConcurrentlyIsRecordedOnce()
    {
        var open = await Scenario.OpenVerificationAsync();
        var eventId = $"evt_{Guid.NewGuid():N}";
        var body = AgeWebhookScenario.Body(open, "verified_adult", eventId);
        var signature = Scenario.Sign(body);
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        Task<HttpResponseMessage>[] callbacks;
        await using (var barrier = await AgeWebhookScenario.HoldAccountLockAsync(connection, open.Account.UserId))
        {
            callbacks = [Scenario.SendAsync(body, signature), Scenario.SendAsync(body, signature)];
            await Scenario.WaitForLockWaitersAsync(2);
            await barrier.CommitAsync(TestContext.Current.CancellationToken);
        }

        var responses = await Task.WhenAll(callbacks);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        Assert.Equal(1, await Scenario.CountReceiptsAsync(eventId));
        Assert.Equal(1, await Verification.CountAuditAsync("identity.age_verification_decided", open.VerificationId));
    }

    [Fact]
    public async Task LateCallbackAfterTheDecisionChangesNothing()
    {
        var open = await Scenario.OpenVerificationAsync();
        using (await Scenario.SendSignedAsync(AgeWebhookScenario.Body(open, "verified_adult")))
        {
        }

        var lateEvent = $"evt_{Guid.NewGuid():N}";
        using var response = await Scenario.SendSignedAsync(AgeWebhookScenario.Body(open, "not_adult", lateEvent));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("VerifiedAdult", Assert.Single(await Verification.VerificationsAsync(open.Account.UserId)).State);
        Assert.Equal("Active", await Verification.AccountStatusAsync(open.Account.UserId));
        Assert.Equal("Rejected", await Scenario.ReceiptStateAsync(lateEvent));
    }

    [Theory]
    [InlineData("wrong-nonce")]
    [InlineData("unknown-reference")]
    [Trait("Threat", "TM-36")]
    public async Task CallbackNotMatchingAServerCreatedRequestChangesNothing(string kind)
    {
        var open = await Scenario.OpenVerificationAsync();
        var eventId = $"evt_{Guid.NewGuid():N}";
        var forged = kind == "wrong-nonce"
            ? open with { Nonce = "nonce-de-outra-solicitacao" }
            : open with { Reference = FakeAgeEligibilityProvider.ReferenceFor(Guid.NewGuid()) };

        using var response = await Scenario.SendSignedAsync(AgeWebhookScenario.Body(forged, "verified_adult", eventId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Rejected", await Scenario.ReceiptStateAsync(eventId));
        Assert.Equal("Pending", Assert.Single(await Verification.VerificationsAsync(open.Account.UserId)).State);
        Assert.Equal("PendingEligibility", await Verification.AccountStatusAsync(open.Account.UserId));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"id\":\"evt_1\",\"type\":\"age_check.completed\",\"reference\":\"r\",\"nonce\":\"n\",\"outcome\":\"adulto\"}")]
    [InlineData("{\"id\":\"evt_1\",\"type\":\"age_check.completed\",\"reference\":\"r\",\"nonce\":\"n\",\"outcome\":\"verified_adult\",\"isAdult\":true}")]
    [InlineData("{\"id\":\"evt\\u0000\",\"type\":\"age_check.completed\",\"reference\":\"r\",\"nonce\":\"n\",\"outcome\":\"verified_adult\"}")]
    [InlineData("não é json")]
    public async Task SignedButMalformedPayloadIsRejectedWithoutReceipt(string json)
    {
        var before = await Scenario.CountReceiptsAsync();
        var body = Encoding.UTF8.GetBytes(json);

        using var response = await Scenario.SendSignedAsync(body);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "WEBHOOK_PAYLOAD_INVALID");
        Assert.Equal(before, await Scenario.CountReceiptsAsync());
    }

    [Fact]
    public async Task BodyOfExactly64KiBIsAcceptedAndOneByteMoreIsRefused()
    {
        var open = await Scenario.OpenVerificationAsync();
        var json = AgeWebhookScenario.Text(AgeWebhookScenario.Body(open, "verified_adult"));
        var limit = FakeAgeCallbackProtocol.MaxBodyBytes;
        var atLimit = Encoding.UTF8.GetBytes(json + new string(' ', limit - Encoding.UTF8.GetByteCount(json)));
        var overLimit = Encoding.UTF8.GetBytes(json + new string(' ', limit + 1 - Encoding.UTF8.GetByteCount(json)));

        using var tooLarge = await Scenario.SendAsync(overLimit, Scenario.Sign(overLimit));
        var statusAfterTooLarge = await Verification.AccountStatusAsync(open.Account.UserId);
        using var accepted = await Scenario.SendAsync(atLimit, Scenario.Sign(atLimit));

        await ProblemAssert.IsProblemAsync(tooLarge, HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE");
        Assert.Equal("PendingEligibility", statusAfterTooLarge);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal("Active", await Verification.AccountStatusAsync(open.Account.UserId));
    }

    [Theory]
    [InlineData("/webhooks/outro")]
    [InlineData("/webhooks/FAKE")]
    [InlineData("/webhooks/none")]
    public async Task ProviderOutsideTheRegisteredAdaptersIsNotFound(string path)
    {
        var open = await Scenario.OpenVerificationAsync();
        var body = AgeWebhookScenario.Body(open, "verified_adult");

        using var response = await Scenario.SendAsync(body, Scenario.Sign(body), path);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal("PendingEligibility", await Verification.AccountStatusAsync(open.Account.UserId));
    }

    [Fact]
    public async Task WithoutAProviderTheWebhookIsNotFound()
    {
        await using var factory = new DuoraWebFactory(_postgres.ConnectionString);
        var scenario = new AgeWebhookScenario(_postgres, factory, _time);
        var body = Encoding.UTF8.GetBytes("{}");

        using var response = await scenario.SendAsync(body, scenario.Sign(body));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task WebhookIgnoresTheBrowserSessionAndNeedsNoAntiforgery()
    {
        // Um navegador com sessão e sem token antiforgery não aprova nada: só a assinatura conta.
        var open = await Scenario.OpenVerificationAsync();
        using var browser = await Verification.Identity.SignedInBrowserAsync(open.Account);
        var body = AgeWebhookScenario.Body(open, "verified_adult");

        using var unsigned = await browser.PostRawJsonAsync(
            AgeWebhookScenario.WebhookPath,
            AgeWebhookScenario.Text(body),
            new MutationHeaders(SendCsrfToken: false));

        await ProblemAssert.IsProblemAsync(unsigned, HttpStatusCode.BadRequest, "WEBHOOK_SIGNATURE_INVALID");
        Assert.Equal("PendingEligibility", await Verification.AccountStatusAsync(open.Account.UserId));
    }

    [Theory]
    [InlineData("verified_adult", "VerifiedAdult", "Active")]
    [InlineData("not_adult", "Rejected", "Restricted")]
    [InlineData("inconclusive", "ManualReview", "PendingEligibility")]
    public async Task CallbackForAPendingPastItsLifetimeIsStillApplied(string outcome, string state, string account)
    {
        // Decisão (M8): a solicitação foi criada pelo servidor e ainda não foi substituída; o prazo só controla
        // quando a pessoa pode pedir outra, não invalida o resultado do fornecedor.
        var open = await Scenario.OpenVerificationAsync();
        _time.Advance(TimeSpan.FromHours(25));

        using var response = await Scenario.SendSignedAsync(AgeWebhookScenario.Body(open, outcome));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(state, Assert.Single(await Verification.VerificationsAsync(open.Account.UserId)).State);
        Assert.Equal(account, await Verification.AccountStatusAsync(open.Account.UserId));
    }

    [Fact]
    public async Task LateNotAdultForAnExpiredRequestStillRestrictsTheAccount()
    {
        var open = await Scenario.OpenVerificationAsync();
        await Verification.ExecuteAsync("update identity.age_verifications set state = 'Expired' where id = $1", open.VerificationId);
        var eventId = $"evt_{Guid.NewGuid():N}";

        using var response = await Scenario.SendSignedAsync(AgeWebhookScenario.Body(open, "not_adult", eventId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Restricted", await Verification.AccountStatusAsync(open.Account.UserId));
        Assert.Equal("Expired", Assert.Single(await Verification.VerificationsAsync(open.Account.UserId)).State);
        Assert.Equal("Processed", await Scenario.ReceiptStateAsync(eventId));
        Assert.Equal(1, await Verification.CountAuditAsync("identity.account_restricted", open.Account.UserId));
        Assert.Equal(1, await Verification.CountAuditAsync("identity.age_verification_late_not_adult", open.VerificationId));
    }

    [Theory]
    [InlineData("verified_adult")]
    [InlineData("inconclusive")]
    public async Task LateVerifiedOrInconclusiveForAnExpiredRequestChangesNothing(string outcome)
    {
        var open = await Scenario.OpenVerificationAsync();
        await Verification.ExecuteAsync("update identity.age_verifications set state = 'Expired' where id = $1", open.VerificationId);
        var eventId = $"evt_{Guid.NewGuid():N}";

        using var response = await Scenario.SendSignedAsync(AgeWebhookScenario.Body(open, outcome, eventId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("PendingEligibility", await Verification.AccountStatusAsync(open.Account.UserId));
        Assert.Equal("Rejected", await Scenario.ReceiptStateAsync(eventId));
    }

    [Fact]
    public async Task SameEventIdWithDifferentBodyIsFlaggedAndHasNoEffect()
    {
        // Decisão do responsável (I3): 204 sem efeito, para o fornecedor não ficar repetindo; alerta no log, sem o
        // corpo, e métrica webhook_event_conflict.
        var logs = new CapturingLoggerProvider();
        await using var factory = new DuoraWebFactory(_postgres.ConnectionString, logs)
        {
            Time = _time,
            Settings = AgeVerificationScenario.FakeProviderSettings(),
        };
        var scenario = new AgeWebhookScenario(_postgres, factory, _time);
        var open = await scenario.OpenVerificationAsync();
        var eventId = $"evt_{Guid.NewGuid():N}";
        using var conflicts = new WebhookConflictCounter();
        using (await scenario.SendSignedAsync(AgeWebhookScenario.Body(open, "inconclusive", eventId)))
        {
        }

        var forged = AgeWebhookScenario.Body(open, "verified_adult", eventId);
        using var response = await scenario.SendSignedAsync(forged);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, await scenario.CountReceiptsAsync(eventId));
        Assert.Equal("ManualReview", Assert.Single(await scenario.Verification.VerificationsAsync(open.Account.UserId)).State);
        Assert.Equal("PendingEligibility", await scenario.Verification.AccountStatusAsync(open.Account.UserId));
        Assert.Equal(1, conflicts.Count);
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Text.Contains(eventId, StringComparison.Ordinal));
        Assert.DoesNotContain(open.Nonce, warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("verified_adult", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameEventIdWithTheSameBodyIsNotAConflict()
    {
        var open = await Scenario.OpenVerificationAsync();
        var body = AgeWebhookScenario.Body(open, "verified_adult");
        using var conflicts = new WebhookConflictCounter();

        using (await Scenario.SendSignedAsync(body))
        using (await Scenario.SendSignedAsync(body))
        {
        }

        Assert.Equal(0, conflicts.Count);
    }

    [Fact]
    public async Task RepeatedSignatureHeaderIsRejected()
    {
        var open = await Scenario.OpenVerificationAsync();
        var body = AgeWebhookScenario.Body(open, "verified_adult");
        var signature = Scenario.Sign(body);

        using var response = await Scenario.SendAsync(body, [signature, signature]);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "WEBHOOK_SIGNATURE_INVALID");
        Assert.Equal("PendingEligibility", await Verification.AccountStatusAsync(open.Account.UserId));
    }

    [Fact]
    public async Task DuplicatedTimestampInTheSignatureIsRejected()
    {
        var open = await Scenario.OpenVerificationAsync();
        var body = AgeWebhookScenario.Body(open, "verified_adult");
        var signature = Scenario.Sign(body);
        var timestamp = signature[..signature.IndexOf(',', StringComparison.Ordinal)];

        using var response = await Scenario.SendAsync(body, $"{timestamp},{signature}");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "WEBHOOK_SIGNATURE_INVALID");
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData(null)]
    public async Task UnexpectedContentTypeIsABadRequestNeverAnUnsupportedMediaType(string? contentType)
    {
        var open = await Scenario.OpenVerificationAsync();
        var body = AgeWebhookScenario.Body(open, "verified_adult");

        using var response = await Scenario.SendAsync(body, [Scenario.Sign(body)], contentType: contentType);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "WEBHOOK_PAYLOAD_INVALID");
        Assert.Equal("PendingEligibility", await Verification.AccountStatusAsync(open.Account.UserId));
    }

    [Theory]
    [InlineData("/webhooks/fake%20")]
    [InlineData("/webhooks/fake.json")]
    [InlineData("/webhooks/fake/extra")]
    [InlineData("/webhooks/%C3%A7")]
    public async Task ProviderNameMustMatchExactly(string path)
    {
        var open = await Scenario.OpenVerificationAsync();
        var body = AgeWebhookScenario.Body(open, "verified_adult");

        using var response = await Scenario.SendAsync(body, Scenario.Sign(body), path);

        // Nome fora do adaptador é 404; caminho que nem casa com a rota cai na política padrão e é 401. Nunca efeito.
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized });
        Assert.Equal("PendingEligibility", await Verification.AccountStatusAsync(open.Account.UserId));
    }

    private async Task AssertNothingChangedAsync(OpenVerification open, string eventId)
    {
        Assert.Equal(0, await Scenario.CountReceiptsAsync(eventId));
        var verification = Assert.Single(await Verification.VerificationsAsync(open.Account.UserId));
        Assert.Equal("Pending", verification.State);
        Assert.Equal(1, verification.Version);
        Assert.Equal("PendingEligibility", await Verification.AccountStatusAsync(open.Account.UserId));
        Assert.Equal(0, await Verification.CountAuditAsync("identity.age_verification_decided", open.VerificationId));
    }
}
