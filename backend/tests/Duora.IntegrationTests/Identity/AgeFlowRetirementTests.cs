using System.Net;

namespace Duora.IntegrationTests.Identity;

public sealed class AgeFlowRetirementTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("VerifiedAdult")]
    [InlineData("NotAdult")]
    [InlineData("Inconclusive")]
    public async Task LegacyCallbacksCannotChangeAccounts(string outcome)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/webhooks/fake", new StringContent("{\"outcome\":\"" + outcome + "\"}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id=$1 and status='PendingEligibility'", account.UserId));
    }

    [Fact]
    public async Task SignedInUserCannotStartAgeVerification()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        using var response = await browser.PostRawJsonAsync("/api/v1/me/age-verifications", "{}");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }
}
