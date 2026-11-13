using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// Trava o contrato OpenAPI das rotas de comando da sala e da inscrição: o documento não muda sem que este
/// teste mude junto. A lista esperada é escrita à mão de propósito. Ela não é derivada do código, então este
/// teste não descobre sozinho um status novo que o endpoint passe a devolver. Quem fixa o que o endpoint de fato
/// devolve são os testes de comportamento: 204 em <c>RoutesParticipationTests</c> e <c>GameDeadlineTests</c>;
/// 400 e 409 em <c>RoutesParticipationTests</c>; 404 em <c>GameDeadlineTests</c> e <c>RoomExitTests</c>; 503 em
/// <c>ParticipationLockContentionTests</c>. Saída, aceite, recusa, inscrição, desistência e check-in não têm
/// corpo nem versão, então não declaram 400 nem 409. Os 401/403 vêm do grupo de rotas.
/// </summary>
public sealed class RoomCommandOpenApiTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(
        "POST",
        "/api/v1/rooms/{roomId}/actions",
        new[] { "204", "400", "401", "403", "404", "409", "503" })]
    [InlineData(
        "POST",
        "/api/v1/rooms/{roomId}/routes-actions",
        new[] { "204", "400", "401", "403", "404", "409", "503" })]
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
