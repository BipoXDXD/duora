using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Duora.IntegrationTests.Http;

/// <summary>
/// Toda rota da API pode devolver 503 de banco indisponível (a sessão vem do banco a cada requisição, mesmo nas rotas
/// anônimas que recebem o cookie), então toda operação declara o 503 <c>ProblemDetails</c> com o <c>Retry-After</c>,
/// no contrato gerado pelo host e no artefato versionado (#492).
/// </summary>
public sealed class ServiceUnavailableOpenApiTests(PostgresFixture postgres) : IAsyncDisposable
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("gerado")]
    [InlineData("publicado")]
    public async Task EveryOperationDeclaresTheTemporaryUnavailabilityWithRetryAfter(string source)
    {
        var contract = await ContractAsync(source);

        var operations = Operations(contract).ToList();
        Assert.NotEmpty(operations);
        Assert.NotNull(contract["components"]!["schemas"]!["ProblemDetails"]);
        var undeclared = operations
            .Where(operation => !DeclaresRetryAfterProblem(operation.Node))
            .Select(operation => operation.Name)
            .ToList();
        Assert.Empty(undeclared);
    }

    private static bool DeclaresRetryAfterProblem(JsonNode operation)
    {
        var response = operation["responses"]?["503"];
        var retryAfter = response?["headers"]?["Retry-After"];
        return response?["content"]?["application/problem+json"]?["schema"]?["$ref"]?.GetValue<string>()
                == "#/components/schemas/ProblemDetails"
            && retryAfter?["schema"]?["type"]?.GetValue<string>() == "integer"
            && retryAfter["schema"]!["minimum"]?.GetValue<int>() == 1;
    }

    private static IEnumerable<(string Name, JsonNode Node)> Operations(JsonNode contract) =>
        from path in contract["paths"]!.AsObject()
        from method in path.Value!.AsObject()
        where Methods.Contains(method.Key)
        select ($"{method.Key.ToUpperInvariant()} {path.Key}", method.Value!);

    private async Task<JsonNode> ContractAsync(string source)
    {
        if (source == "publicado")
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(RepositoryRoot.Find(), "docs", "api", "openapi.json"),
                TestContext.Current.CancellationToken))!;
        }

        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(await document.SerializeAsJsonAsync(
            OpenApiSpecVersion.OpenApi3_1, TestContext.Current.CancellationToken))!;
    }
}
