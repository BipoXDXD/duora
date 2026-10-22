using Duora.Core.Profiles;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Profiles;

public sealed class ProfileLocationContractTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task LocationIsAdditiveAndLegacyEnumAndConditionalHeaderRemain()
    {
        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);
        foreach (var name in new[] { "OwnProfileResponse", "UpdateProfileRequest" })
        {
            var schema = document.Components!.Schemas![name];
            var properties = schema.Properties!;
            Assert.True(properties.ContainsKey("countryCode"));
            Assert.True(properties.ContainsKey("subdivisionCode"));
            Assert.Equal(RegionCode.All.Count + 1, properties["regionCode"].Enum!.Count);
            Assert.Equal(3, properties["countryCode"].Enum!.Count); // BR, US and null.
            Assert.Equal(79, properties["subdivisionCode"].Enum!.Count);
            Assert.DoesNotContain("countryCode", schema.Required ?? new HashSet<string>());
            Assert.DoesNotContain("subdivisionCode", schema.Required ?? new HashSet<string>());
        }
        var patch = document.Paths["/api/v1/me/profile"].Operations![HttpMethod.Patch];
        Assert.Contains(patch.Parameters!, p => p.Name == "If-Match" && p.Required);
        Assert.True(patch.Responses!.ContainsKey("412"));
        Assert.True(patch.Responses.ContainsKey("428"));
    }
}
