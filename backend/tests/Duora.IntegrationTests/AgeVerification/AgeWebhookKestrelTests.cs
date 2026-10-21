using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>Retired callbacks return 410 without parsing provider input, including chunked bodies.</summary>
public sealed class AgeWebhookKestrelTests : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory;

    public AgeWebhookKestrelTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Settings = AgeVerificationScenario.FakeProviderSettings() };
        _factory.UseKestrel(0);
        _factory.StartServer();
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredWebhookDoesNotParseLargeBodiesUnderKestrel(bool chunked)
    {
        var body = new byte[(64 * 1024) + 1];
        var address = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?
            .Addresses.First() ?? throw new InvalidOperationException("Kestrel sem endereço.");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            BaseAddress = new Uri(address),
        });
        using var content = chunked ? new StreamContent(new MemoryStream(body)) : (HttpContent)new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/fake") { Content = content };
        request.Headers.TransferEncodingChunked = chunked;

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await Identity.ProblemAssert.IsProblemAsync(response, HttpStatusCode.Gone, "AGE_VERIFICATION_RETIRED");
    }
}
