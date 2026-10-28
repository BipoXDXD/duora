using System.Collections.Concurrent;
using System.Net;
using Amazon.Runtime;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Transporte do SDK da AWS só de teste: registra host e Authorization de cada chamada e responde sem rede. Prova
/// qual credencial assinou o pedido (o Authorization SigV4 traz <c>Credential=&lt;AccessKeyId&gt;/...</c>).
/// </summary>
internal sealed class RecordingAwsTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpClientFactory
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond = respond;

    public sealed record Call(string Host, string Authorization);

    public ConcurrentQueue<Call> Calls { get; } = new();

    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(new Handler(this));

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;

    public static HttpResponseMessage Xml(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/xml") };

    private sealed class Handler(RecordingAwsTransport owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authorization = request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : "";
            owner.Calls.Enqueue(new(request.RequestUri!.Host, authorization));
            return Task.FromResult(owner._respond(request));
        }
    }
}
