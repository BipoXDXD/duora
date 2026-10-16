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
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
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
        await Scenario.MakeVerifiedAdultAsync(account.UserId, _time.GetUtcNow(), _time.GetUtcNow().AddDays(30));

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
}
