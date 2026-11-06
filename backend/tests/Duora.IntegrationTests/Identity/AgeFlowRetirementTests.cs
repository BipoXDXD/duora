using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

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

    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    public async Task RetiredMeRoutesAnswerGoneWithOnlyTheProblemFields(string method)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);

        using var response = method == "POST"
            ? await browser.PostRawJsonAsync("/api/v1/me/age-verifications", "{}")
            : await browser.GetAsync("/api/v1/me/age-verifications/current");

        var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Gone, "AGE_VERIFICATION_RETIRED");
        Assert.Equal(["code", "status", "title", "traceId", "type"], body.Select(field => field.Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/api/v1/me/age-verifications", "post")]
    [InlineData("/api/v1/me/age-verifications/current", "get")]
    public async Task RetiredMeRoutesDocumentTheGoneProblem(string path, string method)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var provider = factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);
        var generated = JsonNode.Parse(await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, cancellationToken: TestContext.Current.CancellationToken))!;
        var published = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot.Find(), "docs", "api", "openapi.json"), TestContext.Current.CancellationToken))!;

        foreach (var contract in new[] { generated, published })
        {
            var gone = contract["paths"]![path]![method]!["responses"]!["410"];
            Assert.NotNull(gone);
            Assert.Equal("#/components/schemas/ProblemDetails",
                (string?)gone["content"]!["application/problem+json"]!["schema"]!["$ref"]);
        }
    }
}
