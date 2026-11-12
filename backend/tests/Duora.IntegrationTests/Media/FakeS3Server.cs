using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Dublê de S3 em HTTP local que responde o que o teste mandar, sem ser um S3 de verdade: serve para provar como o
/// adaptador classifica 5xx, <c>SlowDown</c>, <c>AccessDenied</c>, queda de conexão e travamento, coisas que o MinIO
/// não produz sob demanda. Sem o corpo de nenhum objeto; credenciais e chaves são sintéticas.
/// </summary>
internal sealed class FakeS3Server : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeS3Server(WebApplication app)
    {
        _app = app;
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var address = addresses.First();
        Url = address.TrimEnd('/');
    }

    /// <summary>Endereço <c>http://127.0.0.1:porta</c>, para <c>S3StorageOptions.ServiceUrl</c>.</summary>
    public string Url { get; }

    public static async Task<FakeS3Server> StartAsync(Func<HttpContext, Task> responder)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Run(responder.Invoke);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new FakeS3Server(app);
    }

    /// <summary>Erro no formato XML do S3: status HTTP, <c>Code</c> e uma mensagem sem dado nenhum.</summary>
    public static Func<HttpContext, Task> Error(HttpStatusCode status, string code) => async context =>
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>synthetic</Message>"
            + "<RequestId>synthetic</RequestId></Error>");
    };

    /// <summary>Aceita a conexão e nunca responde, até o cliente desistir.</summary>
    public static Func<HttpContext, Task> Hang() =>
        context => Task.Delay(Timeout.Infinite, context.RequestAborted);

    /// <summary>Porta que acabou de ser liberada: conectar nela dá "conexão recusada".</summary>
    public static async Task<string> ClosedUrlAsync()
    {
        await using var closed = await StartAsync(Error(HttpStatusCode.OK, "Unused"));
        return closed.Url;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }
}
