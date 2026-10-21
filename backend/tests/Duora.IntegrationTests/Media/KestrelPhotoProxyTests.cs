using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

namespace Duora.IntegrationTests.Media;

public sealed partial class KestrelPhotoUploadTests
{
    [Fact]
    public async Task TheCaddyConfigurationPreservesLegacyCodesConfirmationAndVary()
    {
        var backend = _proxyBackend ?? throw new InvalidOperationException("Backend de teste não iniciado.");
        await TestcontainersSettings.ExposeHostPortsAsync(checked((ushort)backend.Port), Cancellation);
        var root = RepositoryRoot.Find();
        var imageLine = File.ReadLines(Path.Combine(root, "infra", "images", "edge", "Dockerfile"))
            .Single(line => line.StartsWith("FROM caddy:", StringComparison.Ordinal));
        var image = imageLine.Split(' ')[1];
        Assert.Contains("@sha256:", image, StringComparison.Ordinal);
        var config = await File.ReadAllTextAsync(Path.Combine(root, "infra", "caddy", "Caddyfile"), Cancellation);
        // Mesma configuração da borda; só endereço, certificado e porta são de teste. Nenhuma policy é retirada.
        config = config.Replace("web:8080", $"host.testcontainers.internal:{backend.Port.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal);
        using var certificate = SelfSignedCertificate();
        using var key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("Certificado de teste sem chave.");
        await using var proxy = new ContainerBuilder(image)
            .WithEnvironment("DUORA_DOMAIN", "https://:8443")
            .WithEnvironment("DUORA_TLS", "/certs/test.crt /certs/test.key")
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/caddy/Caddyfile")
            .WithResourceMapping(Encoding.UTF8.GetBytes(certificate.ExportCertificatePem()), "/certs/test.crt")
            .WithResourceMapping(Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()), "/certs/test.key")
            .WithPortBinding(8443, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8443))
            .Build();
        await proxy.StartAsync(Cancellation);
        using var http = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, actual, _, _) => actual is not null
                    && string.Equals(actual.GetCertHashString(), certificate.Thumbprint, StringComparison.OrdinalIgnoreCase),
            },
        })
        {
            BaseAddress = new UriBuilder(Uri.UriSchemeHttps, proxy.Hostname, proxy.GetMappedPublicPort(8443)).Uri,
            Timeout = TimeSpan.FromSeconds(60),
        };
        var session = await SignInAsync(http);
        foreach (var empty in new[] { true, false })
        {
            foreach (var selector in new[] { null, "codes-v1", "codes-v1, codes-v1" })
            {
                using var request = Upload(empty ? [] : MediaScenario.JpegLike());
                if (!empty)
                {
                    request.Headers.Remove("Idempotency-Key");
                    request.Headers.Add("Idempotency-Key", "bad key");
                }
                if (selector is not null)
                {
                    request.Headers.Add(PhotoValidationAssertions.Header, selector);
                }
                using var response = await session.SendAsync(request);
                await PhotoValidationAssertions.RefusalAsync(response, empty, selector == "codes-v1");
                Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
                Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
            }
        }
        await PhotoValidationAssertions.NoEffectsAsync(_postgres.ConnectionString, new MediaScenario(_postgres, _kestrel), session.UserId);
    }
}
