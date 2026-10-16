using System.Net;
using Duora.Infrastructure.Identity;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>
/// API-P06: estado mínimo e próximo passo, sem referência, nonce, política nem inferência do fornecedor (TM-38).
/// </summary>
public sealed class AgeVerificationStatusTests : IAsyncDisposable
{
    private static readonly string[] ExpectedKeys = ["continueUrl", "nextStep", "state"];

    private readonly PostgresFixture _postgres;
    private readonly DuoraWebFactory _factory;

    public AgeVerificationStatusTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = AgeVerificationScenario.FakeProviderSettings(),
        };
    }

    private AgeVerificationScenario Scenario => new(_postgres, _factory);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    [Trait("Threat", "TM-38")]
    public async Task AgeVerificationStatusExposesOnlyStateAndNextStep()
    {
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;

        var before = await AgeVerificationScenario.CurrentAsync(browser);
        using (await AgeVerificationScenario.RequestAsync(browser))
        {
        }

        var waiting = await AgeVerificationScenario.CurrentAsync(browser);
        var verification = Assert.Single(await Scenario.VerificationsAsync(account.UserId));
        var flowUrl = $"{AgeVerificationScenario.FakeFlowOrigin}/flow/{FakeAgeEligibilityProvider.ReferenceFor(verification.Id)}";
        await Scenario.MarkProviderFlowReadyAsync(verification.Id, flowUrl);
        var ready = await AgeVerificationScenario.CurrentAsync(browser);

        Assert.Equal(ExpectedKeys, before.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("NotStarted", (string?)before["state"]);
        Assert.Equal("StartVerification", (string?)before["nextStep"]);
        Assert.Null(before["continueUrl"]);

        Assert.Equal(ExpectedKeys, waiting.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("Pending", (string?)waiting["state"]);
        Assert.Equal("WaitForProvider", (string?)waiting["nextStep"]);

        Assert.Equal(ExpectedKeys, ready.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("ContinueWithProvider", (string?)ready["nextStep"]);
        Assert.Equal(flowUrl, (string?)ready["continueUrl"]);
        var text = ready.ToJsonString();
        Assert.DoesNotContain(verification.RequestNonce, text, StringComparison.Ordinal);
        Assert.DoesNotContain(verification.PolicyVersion, text, StringComparison.Ordinal);
        Assert.DoesNotContain(verification.Id.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(verification.Id.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StatusOfAnotherPersonIsNeverShown()
    {
        var (owner, ownerBrowser) = await Scenario.SignedInPendingAccountAsync();
        var (_, otherBrowser) = await Scenario.SignedInPendingAccountAsync();
        using var a = ownerBrowser;
        using var b = otherBrowser;
        using (await AgeVerificationScenario.RequestAsync(ownerBrowser))
        {
        }

        var other = await AgeVerificationScenario.CurrentAsync(otherBrowser);

        Assert.Equal("NotStarted", (string?)other["state"]);
        Assert.Single(await Scenario.VerificationsAsync(owner.UserId));
    }

    [Fact]
    public async Task FlowUrlOutsideTheAllowlistIsNeverShown()
    {
        // Defesa em profundidade: o worker já recusa, e a leitura confere de novo antes de mandar o navegador.
        var (account, browser) = await Scenario.SignedInPendingAccountAsync();
        using var _ = browser;
        using (await AgeVerificationScenario.RequestAsync(browser))
        {
        }

        var verification = Assert.Single(await Scenario.VerificationsAsync(account.UserId));
        await Scenario.MarkProviderFlowReadyAsync(verification.Id, "https://phishing.example/flow");

        var current = await AgeVerificationScenario.CurrentAsync(browser);

        Assert.Equal("WaitForProvider", (string?)current["nextStep"]);
        Assert.Null(current["continueUrl"]);
    }

    [Theory]
    [InlineData("Rejected", "NotAvailable")]
    [InlineData("ManualReview", "WaitForReview")]
    public async Task LaterDecisionIsWhatThePersonSeesOverAnOlderAdultVerification(string laterState, string nextStep)
    {
        var account = await Scenario.Identity.CreateConfirmedAccountAsync();
        await Scenario.MakeVerifiedAdultAsync(account.UserId, DateTimeOffset.UtcNow.AddDays(-400));
        await Scenario.AddDecidedVerificationAsync(account.UserId, laterState, DateTimeOffset.UtcNow.AddDays(-30));
        using var browser = await Scenario.Identity.SignedInBrowserAsync(account);

        var current = await AgeVerificationScenario.CurrentAsync(browser);

        Assert.Equal(laterState, (string?)current["state"]);
        Assert.Equal(nextStep, (string?)current["nextStep"]);
        Assert.Null(current["continueUrl"]);
    }

    [Fact]
    public async Task StatusWithoutSessionIsUnauthenticated()
    {
        using var browser = new Browser(_factory);

        using var response = await browser.GetAsync(AgeVerificationScenario.CurrentPath);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }
}
