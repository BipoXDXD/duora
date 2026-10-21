using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Duora.IntegrationTests.Http;

public sealed class ProblemOpenApiTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("ProblemDetails")]
    [InlineData("HttpValidationProblemDetails")]
    public async Task ProblemExtensionsAreTypedWithoutOpeningTheSchema(string name)
    {
        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);
        var schema = document.Components!.Schemas![name];

        Assert.Equal(JsonSchemaType.String, schema.Properties!["code"].Type);
        Assert.Equal(JsonSchemaType.String, schema.Properties["traceId"].Type);
        Assert.False(schema.AdditionalPropertiesAllowed);
        Assert.DoesNotContain("code", schema.Required ?? new HashSet<string>());
        Assert.DoesNotContain("traceId", schema.Required ?? new HashSet<string>());
    }

    [Theory]
    [InlineData("LoginRequest")]
    [InlineData("PhotoUploadResponse")]
    public async Task NonProblemSchemasKeepTheirClosedShape(string name)
    {
        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);
        var schema = document.Components!.Schemas![name];

        Assert.False(schema.AdditionalPropertiesAllowed);
        Assert.False(schema.Properties!.ContainsKey("code"));
        Assert.False(schema.Properties.ContainsKey("traceId"));
    }
}
