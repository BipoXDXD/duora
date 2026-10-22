using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Duora.Infrastructure.Email;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Duora.IntegrationTests.Outbox;

/// <summary>Servidor HTTP real só em loopback. Nenhuma credencial ou mensagem sai da máquina.</summary>
internal sealed class ResendMailSink : IAsyncDisposable
{
    private readonly WebApplication _app;
    public ConcurrentQueue<CapturedMail> Requests { get; } = new();
    public Func<CancellationToken, Task<int>> Respond { get; set; } = _ => Task.FromResult(200);
    public string? RedirectTo { get; set; }
    private ResendMailSink(WebApplication app) => _app = app;

    public static async Task<ResendMailSink> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        var sink = new ResendMailSink(builder.Build());
        sink._app.MapPost("/emails", async context =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            sink.Requests.Enqueue(new CapturedMail(context.Request.Headers["Idempotency-Key"].ToString(), body.RootElement.Clone()));
            Assert.Equal("Bearer synthetic-only", context.Request.Headers.Authorization.ToString());
            context.Response.StatusCode = await sink.Respond(context.RequestAborted);
            if (sink.RedirectTo is not null)
            {
                context.Response.Headers.Location = sink.RedirectTo;
            }
            await context.Response.WriteAsJsonAsync(new { id = "local-message" }, context.RequestAborted);
        });
        await sink._app.StartAsync(ct);
        return sink;
    }

    public HttpClient CreateClient()
    {
        // Mantém o handler real e os limites de produção; substitui só a origem, por injeção interna de teste.
        var client = ResendEmailSender.CreateClient();
        client.BaseAddress = new Uri(_app.Urls.Single());
        return client;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

internal sealed record CapturedMail(string Key, JsonElement Body)
{
    public string Text => Body.GetProperty("text").GetString()!;
}
