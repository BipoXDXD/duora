using System.Net;
using System.Text.Json;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>
/// API-P05: a solicitação nasce no servidor, uma aberta por pessoa, com o pedido ao fornecedor na outbox (ADR-0024).
/// O navegador nunca informa resultado (TM-35).
/// </summary>
public sealed class AgeVerificationRequestTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    // Segundo inteiro: o PostgreSQL guarda microssegundos e o .NET no Linux tem ticks de 100 ns.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;

    public AgeVerificationRequestTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = _time,
            Settings = AgeVerificationScenario.FakeProviderSettings(),
        };
    }

    private AgeVerificationScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("isAdult", "true")]
    [InlineData("result", "\"VerifiedAdult\"")]
    [InlineData("state", "\"VerifiedAdult\"")]
    [InlineData("outcome", "\"VerifiedAdult\"")]
    [InlineData("expiresAt", "\"2099-01-01T00:00:00Z\"")]
    [Trait("Threat", "TM-35")]
    public async Task AgeVerificationRequestIgnoresClientSuppliedResult(string field, string value)
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using (browser)
        {
            using var response = await browser.PostRawJsonAsync(
                AgeVerificationScenario.RequestsPath,
                $$"""{"{{field}}":{{value}}}""");

            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        }

        Assert.Empty(await Scenario.VerificationsAsync(account.UserId));
        Assert.Equal(0, await Scenario.CountProviderTasksAsync(account.UserId));
        Assert.Equal("PendingEligibility", await Scenario.AccountStatusAsync(account.UserId));
    }

    [Fact]
    public async Task RequestCreatesOnePendingVerificationAndOneProviderTask()
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;

        using var response = await AgeVerificationScenario.RequestAsync(browser);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(AgeVerificationScenario.CurrentPath, response.Headers.Location?.OriginalString);
        var body = await AgeVerificationScenario.ReadJsonAsync(response);
        Assert.Equal("Pending", (string?)body["state"]);
        Assert.Equal("WaitForProvider", (string?)body["nextStep"]);
        Assert.Null(body["continueUrl"]);

        var verification = Assert.Single(await Scenario.VerificationsAsync(account.UserId));
        Assert.Equal("Pending", verification.State);
        Assert.Equal("test-2026-10", verification.PolicyVersion);
        Assert.Equal("fake", verification.Provider);
        Assert.Null(verification.ProviderReference);
        Assert.True(verification.RequestNonce.Length >= 43, "O nonce precisa de ao menos 256 bits em base64url.");
        Assert.Equal(_time.GetUtcNow(), verification.RequestedAt);
        Assert.Null(verification.DecidedAt);
        Assert.Equal(1, await Scenario.CountProviderTasksAsync(account.UserId));
        Assert.Equal(1, await Scenario.CountAuditAsync("identity.age_verification_requested", verification.Id));
        Assert.Equal("PendingEligibility", await Scenario.AccountStatusAsync(account.UserId));
    }

    [Fact]
    public async Task ProviderTaskPayloadCarriesOnlyIds()
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using (browser)
        using (await AgeVerificationScenario.RequestAsync(browser))
        {
        }

        var verification = Assert.Single(await Scenario.VerificationsAsync(account.UserId));
        var payload = await Scenario.Identity.ScalarTextAsync(
            "select payload::text from ops.outbox_events where resource_id = $1",
            verification.Id);
        using var json = JsonDocument.Parse(payload);
        var keys = json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["loginSessionId", "verificationId"], keys);
        Assert.Equal(verification.Id, json.RootElement.GetProperty("verificationId").GetGuid());
        Assert.DoesNotContain(account.Email, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(verification.RequestNonce, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedRequestReturnsTheOpenOneWithoutANewProviderTask()
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;

        using var first = await AgeVerificationScenario.RequestAsync(browser);
        using var second = await AgeVerificationScenario.RequestAsync(browser);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(
            (await AgeVerificationScenario.ReadJsonAsync(first)).ToJsonString(),
            (await AgeVerificationScenario.ReadJsonAsync(second)).ToJsonString());
        Assert.Single(await Scenario.VerificationsAsync(account.UserId));
        Assert.Equal(1, await Scenario.CountProviderTasksAsync(account.UserId));
    }

    [Fact]
    public async Task ConcurrentRequestsCreateOneVerification()
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => AgeVerificationScenario.RequestAsync(browser)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        Assert.Single(await Scenario.VerificationsAsync(account.UserId));
        Assert.Equal(1, await Scenario.CountProviderTasksAsync(account.UserId));
    }

    [Fact]
    public async Task EligibleAccountCannotOpenAnotherRequest()
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow());

        using var response = await AgeVerificationScenario.RequestAsync(browser);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "AGE_VERIFICATION_NOT_AVAILABLE");
        Assert.Single(await Scenario.VerificationsAsync(account.UserId));
        Assert.Equal(0, await Scenario.CountProviderTasksAsync(account.UserId));
    }

    [Theory]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    public async Task RestrictedOrSuspendedAccountCannotRequest(string status)
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        await Scenario.ExecuteAsync("update identity.users set status = $2 where id = $1", account.UserId, status);

        using var response = await AgeVerificationScenario.RequestAsync(browser);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "AGE_VERIFICATION_NOT_AVAILABLE");
        Assert.Empty(await Scenario.VerificationsAsync(account.UserId));
        Assert.Equal(status, await Scenario.AccountStatusAsync(account.UserId));
    }

    [Fact]
    public async Task OpenRequestTheReadDidNotPickIsAConflictNotAServerError()
    {
        // Relógio de outra réplica: a pendente aberta tem requested_at anterior ao de uma expirada. A leitura escolhe a
        // expirada, oferece nova solicitação e o índice de uma aberta por pessoa recusa: 409, nunca 500.
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        await Scenario.AddUndecidedRequestAsync(account.UserId, "Pending", DateTimeOffset.UtcNow.AddMinutes(-20));
        await Scenario.AddUndecidedRequestAsync(account.UserId, "Expired", DateTimeOffset.UtcNow.AddMinutes(-10));

        using var response = await AgeVerificationScenario.RequestAsync(browser);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "AGE_VERIFICATION_NOT_AVAILABLE");
        Assert.Equal(2, (await Scenario.VerificationsAsync(account.UserId)).Count);
        Assert.Equal(0, await Scenario.CountProviderTasksAsync(account.UserId));
    }

    [Fact]
    public async Task RequestAbandonedForItsWholeLifetimeIsExpiredAndReplaced()
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        using (await AgeVerificationScenario.RequestAsync(browser))
        {
        }

        _time.Advance(TimeSpan.FromHours(24));
        // A sessão de 30 minutos ociosa venceu com o avanço; entra de novo.
        using var fresh = await Scenario.Identity.SignedInBrowserAsync(account);
        using var response = await AgeVerificationScenario.RequestAsync(fresh);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var rows = await Scenario.VerificationsAsync(account.UserId);
        Assert.Equal(["Expired", "Pending"], rows.Select(r => r.State));
        Assert.Equal(1, rows[0].Version);
        Assert.Equal(2, await Scenario.CountProviderTasksAsync(account.UserId));
        Assert.Equal(1, await Scenario.CountAuditAsync("identity.age_verification_expired", rows[0].Id));
        Assert.Equal("PendingEligibility", await Scenario.AccountStatusAsync(account.UserId));
    }

    [Fact]
    public async Task RequestOneMinuteBeforeTheLifetimeEndsIsStillTheOpenOne()
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        using (await AgeVerificationScenario.RequestAsync(browser))
        {
        }

        _time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));
        using var fresh = await Scenario.Identity.SignedInBrowserAsync(account);
        using var response = await AgeVerificationScenario.RequestAsync(fresh);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(["Pending"], (await Scenario.VerificationsAsync(account.UserId)).Select(r => r.State));
    }

    [Fact]
    public async Task RequestInManualReviewAnswersAcceptedWithWaitForReview()
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        using (await AgeVerificationScenario.RequestAsync(browser))
        {
        }

        await Scenario.ExecuteAsync("update identity.age_verifications set state = 'ManualReview' where user_id = $1", account.UserId);
        using var response = await AgeVerificationScenario.RequestAsync(browser);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await AgeVerificationScenario.ReadJsonAsync(response);
        Assert.Equal("ManualReview", (string?)body["state"]);
        Assert.Equal("WaitForReview", (string?)body["nextStep"]);
        Assert.Single(await Scenario.VerificationsAsync(account.UserId));
        Assert.Equal(1, await Scenario.CountProviderTasksAsync(account.UserId));
    }

    [Theory]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    public async Task RestrictedOrSuspendedAccountWithAnOpenRequestIsRefused(string status)
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        using (await AgeVerificationScenario.RequestAsync(browser))
        {
        }

        await Scenario.ExecuteAsync("update identity.users set status = $2 where id = $1", account.UserId, status);
        using var response = await AgeVerificationScenario.RequestAsync(browser);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "AGE_VERIFICATION_NOT_AVAILABLE");
        Assert.Equal(["Pending"], (await Scenario.VerificationsAsync(account.UserId)).Select(r => r.State));
        Assert.Equal(1, await Scenario.CountProviderTasksAsync(account.UserId));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Expired")]
    public async Task PendingWhoseProviderTaskGaveUpIsReplaced(string deliveryStatus)
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        using (await AgeVerificationScenario.RequestAsync(browser))
        {
        }

        await Scenario.SetProviderTaskStatusAsync(account.UserId, deliveryStatus);
        // Um minuto depois, para a ordem das duas solicitações não depender do desempate do UUIDv7.
        _time.Advance(TimeSpan.FromMinutes(1));
        var current = await AgeVerificationScenario.CurrentAsync(browser);
        using var response = await AgeVerificationScenario.RequestAsync(browser);

        Assert.Equal("StartVerification", (string?)current["nextStep"]);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(["Expired", "Pending"], (await Scenario.VerificationsAsync(account.UserId)).Select(r => r.State));
        Assert.Equal(2, await Scenario.CountProviderTasksAsync(account.UserId));
    }

    [Fact]
    public async Task PendingWithAProviderFlowIsKeptEvenIfTheTaskEndedInReview()
    {
        // Com referência gravada, a pessoa já tem o fluxo do fornecedor: a solicitação continua valendo.
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        using (await AgeVerificationScenario.RequestAsync(browser))
        {
        }

        var verification = Assert.Single(await Scenario.VerificationsAsync(account.UserId));
        await Scenario.MarkProviderFlowReadyAsync(verification.Id, $"{AgeVerificationScenario.FakeFlowOrigin}/flow/x");
        await Scenario.SetProviderTaskStatusAsync(account.UserId, "Failed");
        using var response = await AgeVerificationScenario.RequestAsync(browser);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("ContinueWithProvider", (string?)(await AgeVerificationScenario.ReadJsonAsync(response))["nextStep"]);
        Assert.Single(await Scenario.VerificationsAsync(account.UserId));
    }

    [Fact]
    public async Task PendingEligibilityWithAnAdultVerificationIsNeverAskedToVerifyAgain()
    {
        // Estado que o fluxo não produz (o callback ativa a conta junto); se surgir, nega sem abrir verificação nova.
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow().AddDays(-1));
        await Scenario.ExecuteAsync("update identity.users set status = 'PendingEligibility' where id = $1", account.UserId);

        using var response = await AgeVerificationScenario.RequestAsync(browser);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "AGE_VERIFICATION_NOT_AVAILABLE");
        Assert.Equal(["VerifiedAdult"], (await Scenario.VerificationsAsync(account.UserId)).Select(r => r.State));
        Assert.Equal(0, await Scenario.CountProviderTasksAsync(account.UserId));
    }

    [Fact]
    public async Task VerifiedAdultCannotOpenAnotherRequestYearsLater()
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow());

        _time.Advance(TimeSpan.FromDays(10 * 366));
        using var fresh = await Scenario.Identity.SignedInBrowserAsync(account);
        using var response = await AgeVerificationScenario.RequestAsync(fresh);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "AGE_VERIFICATION_NOT_AVAILABLE");
        Assert.Single(await Scenario.VerificationsAsync(account.UserId));
        Assert.Equal(0, await Scenario.CountProviderTasksAsync(account.UserId));
        Assert.Equal("Active", await Scenario.AccountStatusAsync(account.UserId));
    }

    [Fact]
    public async Task PrivilegedAccountCannotUseTheAgeVerification()
    {
        // Decisão (rebase sobre o #38): P05 e P06 ficam na política padrão de participante, não em AccountSession.
        // Conta privilegiada é disjunta da de participante (D13) e não participa, então não tem o que verificar.
        var account = await Scenario.Identity.CreatePrivilegedAccountAsync();
        using var browser = await Scenario.Identity.SignedInBrowserAsync(account);

        using var request = await AgeVerificationScenario.RequestAsync(browser);
        using var current = await browser.GetAsync(AgeVerificationScenario.CurrentPath);

        Assert.Equal(HttpStatusCode.Forbidden, request.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, current.StatusCode);
        Assert.Empty(await Scenario.VerificationsAsync(account.UserId));
    }
}
