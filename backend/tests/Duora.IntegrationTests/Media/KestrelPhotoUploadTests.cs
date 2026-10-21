using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Duora.IntegrationTests.Http;
using Duora.IntegrationTests.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Limites de corpo no Kestrel de verdade (o TestServer não os aplica): só a rota de foto aceita até 5 MiB, as demais
/// continuam em 32 KiB, e um corpo lento abaixo da taxa mínima da rota é cortado, sem deixar reserva para trás.
/// HTTPS com certificado autoassinado gerado no teste, porque os cookies e o antiforgery exigem conexão segura.
/// </summary>
[Trait("Acceptance", "T-16")]
public sealed class KestrelPhotoUploadTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly DuoraWebFactory _accounts;
    private readonly DuoraWebFactory _kestrel;
    private HttpClient? _http;

    public KestrelPhotoUploadTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _accounts = new DuoraWebFactory(postgres.ConnectionString);
        _kestrel = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?>
            {
                ["Media:MinUploadBytesPerSecond"] = "2000",
                ["Media:MinUploadRateGracePeriod"] = "00:00:02",
            },
        };
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        using var certificate = SelfSignedCertificate();
        var exported = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
        _kestrel.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(exported)));
        _kestrel.StartServer();
        var addresses = _kestrel.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel sem endereço.");
        var address = addresses.Addresses.Single();
        var thumbprint = exported.Thumbprint;
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            // Confia só no certificado gerado por este teste, pela impressão digital.
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && string.Equals(certificate.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase),
            },
        })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(60),
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        await _kestrel.DisposeAsync();
        await _accounts.DisposeAsync();
    }

    [Theory]
    [InlineData(1024 * 1024)]
    [InlineData(5 * 1024 * 1024)]
    public async Task OneAndFiveMebibytesPassKestrel(int size)
    {
        var session = await SignInAsync();

        using var response = await session.SendAsync(Upload(MediaScenario.JpegLike(size)));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task AnotherRouteStillRefusesMoreThan32KiB()
    {
        var session = await SignInAsync();
        var json = "{\"email\":\"" + new string('a', (32 * 1024) + 1) + "\",\"password\":\"x\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        using var response = await session.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ASlowBodyIsCutAndLeavesNoReservation()
    {
        // O cliente manda ~800 B/s por até 30 s: acima do padrão do Kestrel (240 B/s), abaixo dos 2000 B/s que a rota
        // fixa depois de 2 s. A reserva tem de sumir bem antes de o cliente parar: é a taxa da rota que corta.
        var session = await SignInAsync();
        var media = new MediaScenario(_postgres, _kestrel);
        using var clientGivesUp = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        clientGivesUp.CancelAfter(TimeSpan.FromSeconds(30));
        var sending = session.SendIgnoringFailureAsync(
            Upload(new SlowContent(totalBytes: 64 * 1024, chunk: 16, pause: TimeSpan.FromMilliseconds(20), clientGivesUp.Token)),
            clientGivesUp.Token);

        var reserved = await WaitUntilAsync(async () => (await media.UploadsOfAsync(session.UserId)).Count == 1, TimeSpan.FromSeconds(10));
        var released = reserved
            && await WaitUntilAsync(async () => (await media.UploadsOfAsync(session.UserId)).Count == 0, TimeSpan.FromSeconds(12));
        var cutBeforeTheClientGaveUp = !clientGivesUp.IsCancellationRequested;
        await clientGivesUp.CancelAsync();
        await sending;

        Assert.True(reserved, "A reserva Pending nem chegou a ser criada.");
        Assert.True(released, "A reserva do corpo lento não foi desfeita.");
        Assert.True(cutBeforeTheClientGaveUp, "Quem encerrou foi o cliente, não a taxa mínima da rota.");
        Assert.Equal(0, media.StoredObjectCount());
    }

    [Theory]
    [InlineData("empty", "pt-BR")]
    [InlineData("empty", "en-US")]
    [InlineData("missing-key", "en-US")]
    [InlineData("invalid-key", "en-US")]
    [InlineData("codes-format", "en-US")]
    [InlineData("codes-format", "pt-BR")]
    [InlineData("codes-key", "en-US")]
    [InlineData("duplicate-format", "en-US")]
    [InlineData("list-format", "en-US")]
    [InlineData("case-format", "en-US")]
    [InlineData("empty-format", "en-US")]
    [InlineData("unknown-format", "en-US")]
    [InlineData("unsupported-type", "en-US")]
    public async Task UploadRefusalsSatisfyTheGeneratedAndPublishedErrorSchemas(string scenario, string locale)
    {
        var session = await SignInAsync();
        var empty = scenario is "empty" or "codes-format" or "duplicate-format" or "list-format" or "case-format" or "empty-format" or "unknown-format";
        var content = new ByteArrayContent(empty ? [] : MediaScenario.JpegLike());
        content.Headers.ContentType = new MediaTypeHeaderValue(scenario == "unsupported-type" ? "text/html" : "image/jpeg");
        using var request = Upload(content);
        request.Headers.Add("Accept-Language", locale);
        if (scenario is "missing-key" or "invalid-key" or "codes-key")
        {
            request.Headers.Remove("Idempotency-Key");
            if (scenario is "invalid-key" or "codes-key")
            {
                request.Headers.Add("Idempotency-Key", "bad key");
            }
        }
        if (scenario is "codes-format" or "codes-key")
        {
            request.Headers.Add("Duora-Validation-Format", "codes-v1");
        }

        if (scenario == "duplicate-format")
        {
            request.Headers.TryAddWithoutValidation("Duora-Validation-Format", "codes-v1");
            request.Headers.TryAddWithoutValidation("Duora-Validation-Format", "codes-v1");
        }
        if (scenario == "list-format")
        {
            request.Headers.TryAddWithoutValidation("Duora-Validation-Format", "codes-v1, codes-v1");
        }
        if (scenario == "case-format")
        {
            request.Headers.TryAddWithoutValidation("Duora-Validation-Format", "CODES-V1");
        }
        if (scenario == "empty-format")
        {
            request.Headers.TryAddWithoutValidation("Duora-Validation-Format", "");
        }
        if (scenario == "unknown-format")
        {
            request.Headers.TryAddWithoutValidation("Duora-Validation-Format", "unknown");
        }
        using var response = await session.SendAsync(request);

        var unsupported = scenario == "unsupported-type";
        var body = await ProblemAssert.IsProblemAsync(
            response,
            unsupported ? HttpStatusCode.UnsupportedMediaType : HttpStatusCode.BadRequest,
            unsupported ? "UNSUPPORTED_MEDIA_TYPE" : "VALIDATION_FAILED");
        if (!unsupported)
        {
            var errors = body["errors"]!.AsObject();
            var field = empty ? "body" : "Idempotency-Key";
            Assert.Equal([field], errors.Select(p => p.Key));
            Assert.Equal(
                scenario == "codes-format" ? "PHOTO_BODY_EMPTY" : scenario == "codes-key" ? "IDEMPOTENCY_KEY_INVALID" : empty ? "A foto está vazia." : "Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'.",
                errors[field]![0]!.GetValue<string>());
            Assert.Single(errors[field]!.AsArray());
        }
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", Assert.Single(response.Headers.Pragma).Name);
        Assert.Equal(scenario is "codes-format" or "codes-key", response.Headers.Contains("Duora-Validation-Format"));
        if (scenario is "codes-format" or "codes-key")
        {
            Assert.Equal("codes-v1", Assert.Single(response.Headers.GetValues("Duora-Validation-Format")));
        }
        if (!unsupported)
        {
            Assert.Contains("Duora-Validation-Format", response.Headers.Vary);
        }
        Assert.False(response.Headers.Contains("Retry-After"));
        var media = new MediaScenario(_postgres, _kestrel);
        Assert.Empty(await media.UploadsOfAsync(session.UserId));
        Assert.Equal(0, media.StoredObjectCount());
        await ProblemSchemaAssertions.MatchesUploadRefusalAsync(_kestrel, response, body);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "codes-v1")]
    [InlineData(true, null)]
    [InlineData(true, "codes-v1")]
    public async Task NegotiationPreservesMissingAndExcessiveLengthOverRealHttps(bool excessive, string? selector)
    {
        var session = await SignInAsync();
        using HttpContent content = excessive
            ? new ByteArrayContent(MediaScenario.JpegLike((5 * 1024 * 1024) + 1))
            : new StreamContent(new ForwardOnlyStream(MediaScenario.JpegLike()));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var request = Upload(content);
        // Let the server refuse the declared size before this client transmits a body it will not read.
        request.Headers.ExpectContinue = true;
        request.Headers.Remove("Idempotency-Key");
        if (selector is not null)
        {
            request.Headers.Add("Duora-Validation-Format", selector);
        }
        using var response = await session.SendAsync(request);
        await ProblemAssert.IsProblemAsync(response,
            excessive ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.LengthRequired,
            excessive ? "PAYLOAD_TOO_LARGE" : "LENGTH_REQUIRED");
        Assert.False(response.Headers.Contains("Duora-Validation-Format"));
        var media = new MediaScenario(_postgres, _kestrel);
        Assert.Empty(await media.UploadsOfAsync(session.UserId));
        Assert.Equal(0, media.StoredObjectCount());
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100, Cancellation);
        }

        return false;
    }

    private static HttpRequestMessage Upload(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return Upload(content);
    }

    private static HttpRequestMessage Upload(HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MediaScenario.PhotosPath) { Content = content };
        request.Headers.Add("Idempotency-Key", MediaScenario.NewKey());
        return request;
    }

    private async Task<KestrelSession> SignInAsync()
    {
        var account = await new IdentityScenario(_postgres, _accounts).CreateConfirmedAccountAsync();
        var session = new KestrelSession(_http ?? throw new InvalidOperationException("Servidor não iniciado."), account.UserId);
        using (var csrf = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf")))
        {
            Assert.Equal(HttpStatusCode.NoContent, csrf.StatusCode);
        }

        using var login = await session.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = account.Email, password = account.Password }),
        });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return session;
    }

    private static X509Certificate2 SelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <summary>Cookies, CSRF e cabeçalhos same-origin à mão: o pote de cookies do HttpClient não serve aqui.</summary>
    private sealed class KestrelSession(HttpClient http, Guid userId)
    {
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

        public Guid UserId { get; } = userId;

        /// <summary>Envio cujo desfecho no cliente não interessa (conexão cortada, cancelada ou resposta de erro).</summary>
        public async Task SendIgnoringFailureAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await SendCoreAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
            {
                // O Kestrel fecha a conexão ou o teste desiste; o que se confere é o estado do servidor.
            }
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => SendCoreAsync(request, Cancellation);

        private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using (request)
            {
                if (_cookies.Count > 0)
                {
                    request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
                }

                if (_cookies.TryGetValue("XSRF-TOKEN", out var token))
                {
                    request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(token));
                }

                request.Headers.Add("Origin", DuoraWebFactory.PublicOrigin);
                request.Headers.Add("Sec-Fetch-Site", "same-origin");
                var response = await http.SendAsync(request, cancellationToken);
                foreach (var header in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
                {
                    var pair = header.Split(';', 2)[0].Split('=', 2);
                    _cookies[pair[0]] = pair[1];
                }

                return response;
            }
        }
    }

    /// <summary>Corpo que pinga poucos bytes por vez, abaixo da taxa mínima da rota.</summary>
    private sealed class SlowContent : HttpContent
    {
        private readonly int _totalBytes;
        private readonly int _chunk;
        private readonly TimeSpan _pause;
        private readonly CancellationToken _stop;

        public SlowContent(int totalBytes, int chunk, TimeSpan pause, CancellationToken stop)
        {
            _totalBytes = totalBytes;
            _chunk = chunk;
            _pause = pause;
            _stop = stop;
            Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var bytes = MediaScenario.JpegLike(_totalBytes);
            for (var offset = 0; offset < bytes.Length; offset += _chunk)
            {
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(_chunk, bytes.Length - offset)), _stop);
                await stream.FlushAsync(_stop);
                await Task.Delay(_pause, _stop);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _totalBytes;
            return true;
        }
    }
}
