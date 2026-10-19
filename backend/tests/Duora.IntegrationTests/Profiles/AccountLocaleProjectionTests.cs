using System.Net;
using System.Text.Json.Nodes;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Profiles;

public sealed class AccountLocaleProjectionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task OwnAccountReturnsPersistedDisplayLocaleWithoutDeclaringCommunicationLanguages()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var user = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(user);
        using var save = await browser.PutAsync("/api/v1/me/locale", """{"locale":"pt-BR"}""",
            new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = "\"0\"" }));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var response = await browser.GetAsync("/api/v1/me");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("pt-BR", (string?)body["preferredLocale"]);
        using var preferences = await browser.GetAsync("/api/v1/me/preferences");
        var declaration = JsonNode.Parse(await preferences.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Empty(declaration["communicationLanguages"]!.AsArray());
    }
}
