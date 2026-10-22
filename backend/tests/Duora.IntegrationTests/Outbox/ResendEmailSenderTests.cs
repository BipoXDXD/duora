using System.Net;
using System.Text.Json;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Outbox;

public sealed class ResendEmailSenderTests
{
    private static readonly TransactionalEmail Email = new(Address(), "Confirme • Confirm", "Código sintético 12345678");
    private static EmailAddress Address()
    {
        Assert.True(EmailAddress.TryParse("synthetic@example.test", out var address));
        return address;
    }

    private const string DeliveryId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task SendsPlainTextWithStableIdempotencyAndBearerToResend()
    {
        var calls = 0;
        using var handler = new CaptureHandler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.resend.com/emails", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-only", request.Headers.Authorization.Parameter);
            Assert.Equal(DeliveryId, Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("Duora <sender@example.test>", body.RootElement.GetProperty("from").GetString());
            Assert.Equal("synthetic@example.test", body.RootElement.GetProperty("to")[0].GetString());
            Assert.Equal(Email.Subject, body.RootElement.GetProperty("subject").GetString());
            Assert.Equal(Email.TextBody, body.RootElement.GetProperty("text").GetString());
            Assert.False(body.RootElement.TryGetProperty("html", out _));
            return Response(HttpStatusCode.OK, "{\"id\":\"synthetic-message\"}");
        });
        using var client = Client(handler);
        var sender = new ResendEmailSender(client, Options.Create(Settings()));
        await sender.SendAsync(Email, DeliveryId, TestContext.Current.CancellationToken);
        await sender.SendAsync(Email, DeliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RealTransportDoesNotFollowRedirects()
    {
        await using var sink = await ResendMailSink.StartAsync(TestContext.Current.CancellationToken);
        using var client = sink.CreateClient();
        sink.RedirectTo = new Uri(client.BaseAddress!, "emails").AbsoluteUri;
        sink.Respond = _ => Task.FromResult(302);
        var sender = new ResendEmailSender(client, Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(Email, DeliveryId, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Found, error.StatusCode);
        Assert.Single(sink.Requests);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(302)]
    public async Task RejectsNonSuccessWithoutExposingResponse(int status)
    {
        using var handler = new CaptureHandler((_, _) => Task.FromResult(Response((HttpStatusCode)status, "PRIVATE_BODY synthetic@example.test synthetic-only")));
        using var client = Client(handler);
        var sender = new ResendEmailSender(client, Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(Email, DeliveryId, TestContext.Current.CancellationToken));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain("PRIVATE_BODY", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-only", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic@example.test", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":null}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("not-json")]
    public async Task DoesNotAcknowledgeMalformedSuccess(string body)
    {
        using var handler = new CaptureHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));
        using var client = Client(handler);
        var sender = new ResendEmailSender(client, Options.Create(Settings()));
        await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(Email, DeliveryId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PropagatesCancellationToTransport()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new CaptureHandler(async (_, ct) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response(HttpStatusCode.OK, "{}");
        });
        using var client = Client(handler);
        var sender = new ResendEmailSender(client, Options.Create(Settings()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Email, DeliveryId, cancellation.Token));
    }

    [Theory]
    [InlineData("Unknown", "", "")]
    [InlineData("Resend", "", "sender@example.test")]
    [InlineData("Resend", "synthetic-only", "")]
    [InlineData("Resend", "synthetic-only", "invalid")]
    [InlineData("Resend", "synthetic-only", "sender@example.test\r\nBcc: other@example.test")]
    public void RejectsInvalidConfiguration(string provider, string apiKey, string from)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Provider"] = provider,
            ["Email:Resend:ApiKey"] = apiKey,
            ["Email:Resend:From"] = from,
        }).Build());
        services.AddLogging();
        services.AddTransactionalEmailDispatch();
        using var container = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => container.GetRequiredService<IOptions<EmailTransportOptions>>().Value);
    }

    internal static EmailTransportOptions Settings() => new()
    {
        Provider = "Resend",
        Resend = new() { ApiKey = "synthetic-only", From = "Duora <sender@example.test>" },
    };

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://api.resend.com/") };
    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };
    private sealed class CaptureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
