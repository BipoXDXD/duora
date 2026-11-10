using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// O contrato das rotas de comando da sala e da inscrição lista exatamente os status que o endpoint devolve: 204 de
/// sucesso (ou replay), 400 VALIDATION_FAILED, 404 de sala ou evento inexistente, alheio, encerrado ou com prazo vencido,
/// 409 de versão, 503 de contenção de lock e os 401/403 do grupo. Saída, aceite, recusa, inscrição, desistência e check-in não têm
/// corpo nem versão, então não têm 400 nem 409.
/// </summary>
public sealed class RoomCommandOpenApiTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("POST", "/api/v1/rooms/{roomId}/actions", new[] { "204", "400", "401", "403", "404", "409", "503" })]
    [InlineData("POST", "/api/v1/rooms/{roomId}/routes-actions", new[] { "204", "400", "401", "403", "404", "409", "503" })]
    [InlineData("POST", "/api/v1/rooms/{roomId}/exit", new[] { "204", "401", "403", "404", "503" })]
    [InlineData("POST", "/api/v1/rooms/{roomId}/accept", new[] { "204", "401", "403", "404", "503" })]
    [InlineData("POST", "/api/v1/rooms/{roomId}/reject", new[] { "204", "401", "403", "404", "503" })]
    [InlineData("PUT", "/api/v1/events/{eventId}/registration", new[] { "204", "401", "403", "404", "503" })]
    [InlineData("DELETE", "/api/v1/events/{eventId}/registration", new[] { "204", "401", "403", "404", "503" })]
    [InlineData("POST", "/api/v1/events/{eventId}/check-in", new[] { "204", "401", "403", "404", "503" })]
    public async Task CommandDeclaresExactlyTheStatusesItReturns(string method, string path, string[] expected)
    {
        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);
        var operation = document.Paths[path].Operations![HttpMethod.Parse(method)];

        Assert.Equal(expected, operation.Responses!.Keys.Order(StringComparer.Ordinal));
        Assert.True(operation.Responses["204"].Content is null or { Count: 0 });
        foreach (var (status, response) in operation.Responses)
        {
            if (status != "204")
            {
                Assert.Equal("application/problem+json", Assert.Single(response.Content!.Keys));
            }
        }
    }
}
