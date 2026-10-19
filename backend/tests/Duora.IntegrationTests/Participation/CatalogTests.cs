using System.Net;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Participation;

public sealed class CatalogTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ParticipantCannotPublishCatalog()
    {
        var scenario = new IdentityScenario(postgres, _factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        using var response = await browser.PostJsonAsync("/api/v1/admin/experience-versions", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
