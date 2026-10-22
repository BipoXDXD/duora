using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Duora.Core.Notifications;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Email;

/// <summary>
/// Transporte privado: sem logging de HttpClientFactory, redirects ou cookies. O dispatcher controla timeout,
/// backoff e fencing; não há retry escondido aqui. A chave estável é repassada ao Resend (janela de 24 h).
/// </summary>
internal sealed class ResendEmailSender(HttpClient client, IOptions<EmailTransportOptions> options) : ITransactionalEmailSender
{
    internal static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        BaseAddress = new Uri("https://api.resend.com/"),
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = 16 * 1024,
    };

    public async Task SendAsync(TransactionalEmail email, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        if (idempotencyKey.Length != 32 || !idempotencyKey.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("Expected an outbox delivery ID.", nameof(idempotencyKey));
        }

        var configuration = options.Value.Resend;
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ApiKey);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(new
        {
            from = configuration.From,
            to = new[] { email.To.Value },
            subject = email.Subject,
            text = email.TextBody,
        });
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Não materializa o corpo de erro no diagnóstico: pode conter endereço, token ou chave.
            throw new HttpRequestException("Resend rejected the email request.", null, response.StatusCode);
        }

        try
        {
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (result.RootElement.ValueKind == JsonValueKind.Object
                && result.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(id.GetString()))
            {
                return;
            }
        }
        catch (JsonException)
        {
            // Não propaga texto do provedor em exceções/logs.
        }
        throw new HttpRequestException("Resend returned an invalid acknowledgement.");
    }
}
