using System.Net;

namespace Duora.IntegrationTests.AgeVerification;

public sealed class AgeVerificationStatusTests(PostgresFixture postgres)
{
    [Fact]
    public async Task RetiredStatusNeverExposesHistoricalAgeResults()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new AgeVerificationScenario(postgres, factory);
        var account = await scenario.Identity.CreateConfirmedAccountAsync();
        await scenario.MakeVerifiedAdultAsync(account.UserId, DateTimeOffset.UtcNow);
        using var browser = await scenario.Identity.SignedInBrowserAsync(account);
        using var response = await browser.GetAsync(AgeVerificationScenario.CurrentPath);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.DoesNotContain("VerifiedAdult", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
