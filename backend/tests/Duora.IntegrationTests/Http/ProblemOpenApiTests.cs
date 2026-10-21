using System.Text.Json.Nodes;
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

    [Fact]
    public async Task UploadFormatIsOptionalAndOnlyTheTwoValidationRefusalsCanConfirmIt()
    {
        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);
        var generated = JsonNode.Parse(await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, cancellationToken: TestContext.Current.CancellationToken))!;
        var published = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot.Find(), "docs", "api", "openapi.json"), TestContext.Current.CancellationToken))!;
        foreach (var contract in new[] { generated, published })
        {
            var upload = contract["paths"]!["/api/v1/me/photos"]!["post"]!;
            var selector = Assert.Single(upload["parameters"]!.AsArray(), p => (string?)p!["name"] == "Duora-Validation-Format")!;
            Assert.Equal("header", (string?)selector["in"]);
            Assert.False((bool?)selector["required"] ?? false);
            Assert.Equal("string", (string?)selector["schema"]!["type"]);
            Assert.Null(selector["schema"]!["enum"]);
            var key = Assert.Single(upload["parameters"]!.AsArray(), p => (string?)p!["name"] == "Idempotency-Key")!;
            Assert.True((bool?)key["required"]);
            Assert.Equal(1, (int?)key["schema"]!["minLength"]);
            Assert.Equal(64, (int?)key["schema"]!["maxLength"]);
            Assert.Equal("^[A-Za-z0-9_-]+$", (string?)key["schema"]!["pattern"]);
            var badRequest = upload["responses"]!["400"]!;
            Assert.False((bool?)badRequest["headers"]!["Duora-Validation-Format"]!["required"] ?? false);
            Assert.Equal("string", (string?)badRequest["headers"]!["Duora-Validation-Format"]!["schema"]!["type"]);
            Assert.Equal("string", (string?)badRequest["headers"]!["Vary"]!["schema"]!["type"]);
            foreach (var (status, response) in upload["responses"]!.AsObject())
            {
                if (status != "400")
                {
                    Assert.Null(response!["headers"]?["Duora-Validation-Format"]);
                }
            }
        }
    }
}
