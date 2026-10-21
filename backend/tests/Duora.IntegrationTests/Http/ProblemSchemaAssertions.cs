using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Duora.IntegrationTests.Http;

/// <summary>Valida a resposta HTTP contra o schema gerado no host e o artefato público versionado, sem reescrever nenhum deles.</summary>
internal static class ProblemSchemaAssertions
{
    public static async Task MatchesUploadRefusalAsync(DuoraWebFactory factory, HttpResponseMessage response, JsonObject body)
    {
        var provider = factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);
        var generated = JsonNode.Parse(await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1))!.AsObject();
        var published = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot.Find(), "docs", "api", "openapi.json"),
            TestContext.Current.CancellationToken))!.AsObject();

        foreach (var contract in new[] { generated, published })
        {
            var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            var reference = contract["paths"]!["/api/v1/me/photos"]!["post"]!["responses"]![status]!
                ["content"]!["application/problem+json"]!["schema"]!["$ref"]!.GetValue<string>();
            var definition = contract["components"]!["schemas"]![reference.Split('/')[^1]]!.DeepClone().AsObject();
            definition["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            var schema = JsonSchema.FromText(definition.ToJsonString());
            var result = schema.Evaluate(body, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, JsonSerializer.Serialize(result));

            var unexpected = body.DeepClone().AsObject();
            unexpected["unexpected"] = "not a documented extension";
            Assert.False(schema.Evaluate(unexpected).IsValid);

            foreach (var name in new[] { "code", "traceId" })
            {
                var wrongType = body.DeepClone().AsObject();
                wrongType[name] = 42;
                Assert.False(schema.Evaluate(wrongType).IsValid);
                wrongType[name] = null;
                Assert.False(schema.Evaluate(wrongType).IsValid);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var wrongErrors = body.DeepClone().AsObject();
                wrongErrors["errors"] = JsonNode.Parse("""{"body":[42]}""");
                Assert.False(schema.Evaluate(wrongErrors).IsValid);
            }
        }
    }
}
