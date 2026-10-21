using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Profiles;

public sealed class RegionalPreferenceContractTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ContractDocumentsExplicitModesAndOnlyTheEnabledCountry()
    {
        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);
        var schemas = document.Components!.Schemas!;
        Assert.Equal(3, schemas["OwnRegionalPreferencesResponse"].Properties!["mode"].Enum?.Count);
        var request = schemas["UpdateRegionalPreferencesRequest"];
        Assert.Equal(2, request.Properties!["mode"].Enum?.Count);
        Assert.Contains("mode", request.Required!);
        Assert.Contains("countryCodes", request.Required!);
        Assert.Single(request.Properties["countryCodes"].Items!.Enum!);
        Assert.Equal("BR", request.Properties["countryCodes"].Items!.Enum![0]!.GetValue<string>());
        var put = document.Paths["/api/v1/me/regional-preferences"].Operations![HttpMethod.Put];
        Assert.Contains(put.Parameters!, p => p.Name == "If-Match" && p.Required);
        Assert.Contains("412", put.Responses!.Keys);
        Assert.Contains("428", put.Responses.Keys);
    }
}
