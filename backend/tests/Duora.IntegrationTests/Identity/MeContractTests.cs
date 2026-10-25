using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// A SPA decide a fase restrita pelo 403 de <c>GET /me</c> (auth.md, "SPA: senha inicial administrativa"). O contrato
/// gerado precisa nomear o código, para o cliente não depender de conhecimento fora do <c>openapi.json</c>.
/// </summary>
public sealed class MeContractTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GetMeDocumentsTheTemporaryPasswordRestriction()
    {
        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");

        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);

        var responses = document.Paths["/api/v1/me"].Operations![System.Net.Http.HttpMethod.Get].Responses!;
        Assert.Contains("PASSWORD_CHANGE_REQUIRED", responses["403"].Description, StringComparison.Ordinal);
        Assert.True(responses.ContainsKey("503"), "GET /me lê o banco e responde 503 quando ele está indisponível.");
        Assert.Contains("PrivateSessionContextResponse", document.Components!.Schemas!.Keys);
        Assert.Contains("PrivateSessionRestrictionResponse", document.Components.Schemas.Keys);
    }
}
