using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// O contrato das rotas de comando da sala lista exatamente os status que o endpoint devolve: 204 de sucesso (ou replay),
/// 400 VALIDATION_FAILED, 404 de sala inexistente, alheia, encerrada ou com prazo vencido, 409 de versão,
/// 503 de contenção de lock e os 401/403 do grupo. A saída não tem corpo nem versão, então não tem 400 nem 409.
/// </summary>
public sealed class RoomCommandOpenApiTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("/api/v1/rooms/{roomId}/actions", new[] { "204", "400", "401", "403", "404", "409", "503" })]
    [InlineData("/api/v1/rooms/{roomId}/routes-actions", new[] { "204", "400", "401", "403", "404", "409", "503" })]
    [InlineData("/api/v1/rooms/{roomId}/exit", new[] { "204", "401", "403", "404", "503" })]
    public async Task RoomCommandDeclaresExactlyTheStatusesItReturns(string path, string[] expected)
    {
        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);
        var post = document.Paths[path].Operations![HttpMethod.Post];

        Assert.Equal(expected, post.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.True(post.Responses["204"].Content is null or { Count: 0 });
        foreach (var (status, response) in post.Responses)
        {
            if (status != "204")
            {
                Assert.Equal("application/problem+json", Assert.Single(response.Content!.Keys));
            }
        }
    }
}
