using System.Net;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>API-P05 legado foi removido: nenhum payload pode iniciar verificação etária.</summary>
public sealed class AgeVerificationRequestTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"isAdult\":true}")]
    [InlineData("{\"age\":25}")]
    public async Task LegacyRequestsAreGoneAndNeverQueueWork(string body)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        using var response = await browser.PostRawJsonAsync(AgeVerificationScenario.RequestsPath, body);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.age_verifications where user_id=$1", account.UserId));
    }
}
