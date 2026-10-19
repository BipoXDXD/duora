using System.Net;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>Resultados antigos, inclusive autenticados ou repetidos, não são mais processados.</summary>
public sealed class AgeWebhookTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("VerifiedAdult", "Active")]
    [InlineData("NotAdult", "Active")]
    [InlineData("NotAdult", "Restricted")]
    [InlineData("VerifiedAdult", "Suspended")]
    [InlineData("Inconclusive", "PendingEligibility")]
    public async Task CallbackAndReplayCannotChangeAnyAccountState(string outcome, string status)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        await scenario.ScalarAsync("update identity.users set status=$2 where id=$1 returning 1", account.UserId, status);
        using var client = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { outcome, userId = account.UserId }));
            using var response = await client.PostAsync("/webhooks/fake", content, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        }

        Assert.Equal(1, await scenario.ScalarAsync("select count(*) from identity.users where id=$1 and status=$2", account.UserId, status));
    }
}
