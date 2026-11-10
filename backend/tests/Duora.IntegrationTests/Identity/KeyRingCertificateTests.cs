using System.Net;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Spec 11.2: o key ring fica cifrado em repouso com um certificado entregue como segredo. Quem tem só o volume (ou
/// um backup dele) não abre as chaves; a troca de certificado mantém o antigo para ler as chaves que ele cifrou.
/// </summary>
public sealed class KeyRingCertificateTests : IDisposable
{
    private const string Certificate = "DataProtection:Certificate";
    private const string PreviousCertificates = "DataProtection:PreviousCertificates";
    private const string RequireCertificate = "DataProtection:RequireCertificate";

    // Ids de LoggerMessage do Data Protection (LoggingExtensions.cs, ASP.NET Core v10.0.14).
    private const string KeyResolverCategory = "Microsoft.AspNetCore.DataProtection.KeyManagement.DefaultKeyResolver";
    private const int KeyIneligibleAfterRetries = 12;
    private const int RetryingKeyAfterFailure = 73;

    // Sem chave padrão utilizável, o KeyRingProvider cria uma chave e resolve o anel de novo (CreateCacheableKeyRingCore,
    // v10.0.14): a chave que não decifra é avaliada nas duas resoluções da partida a frio.
    private const int ResolutionsAtColdStartWithoutUsableKey = 2;

    private static readonly TimeSpan DefaultKeyLifetime = TimeSpan.FromDays(90);

    private readonly PostgresFixture _postgres;
    private readonly KeyRingHosts _hosts;
    private readonly DirectoryInfo _volume = Directory.CreateTempSubdirectory("duora-keys-volume-");
    private readonly TestCertificate _first = TestCertificates.Create("duora-key-ring-1");
    private readonly TestCertificate _second = TestCertificates.Create("duora-key-ring-2");

    public KeyRingCertificateTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _hosts = new KeyRingHosts(postgres);
    }

    public void Dispose() => _volume.Delete(recursive: true);

    [Fact]
    public async Task KeyIsWrittenToTheVolumeEncryptedWithTheCertificate()
    {
        await _hosts.SignInOnAFreshHostAsync(_volume, With(_first));

        var key = File.ReadAllText(Assert.Single(_volume.GetFiles("key-*.xml")).FullName);
        Assert.Contains("EncryptedData", key, StringComparison.Ordinal);
        Assert.Contains(_first.RawBase64, key, StringComparison.Ordinal);
        Assert.DoesNotContain("<value>", key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VolumeWithoutTheCertificateDoesNotOpenTheSession()
    {
        var cookie = await _hosts.SignInOnAFreshHostAsync(_volume, With(_first));

        await using var withoutCertificate = _hosts.HostOn(_volume);
        using var response = await GetMeAsync(withoutCertificate, cookie);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task VolumeWithAnotherCertificateDoesNotOpenTheSession()
    {
        var cookie = await _hosts.SignInOnAFreshHostAsync(_volume, With(_first));

        await using var otherCertificate = _hosts.HostOn(_volume, With(_second));
        using var response = await GetMeAsync(otherCertificate, cookie);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task SameCertificateAfterRecreatingTheHostKeepsTheSession()
    {
        var cookie = await _hosts.SignInOnAFreshHostAsync(_volume, With(_first));

        await using var recreated = _hosts.HostOn(_volume, With(_first));
        using var response = await GetMeAsync(recreated, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CertificateRotationKeepsTheSessionWithThePreviousCertificate()
    {
        var cookie = await _hosts.SignInOnAFreshHostAsync(_volume, With(_first));

        await using var rotated = _hosts.HostOn(_volume, With(_second, previous: _first));
        using var response = await GetMeAsync(rotated, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task KeysCreatedAfterTheRotationAreEncryptedWithTheNewCertificate()
    {
        await _hosts.SignInOnAFreshHostAsync(_volume, With(_first));
        var firstKey = Assert.Single(_volume.GetFiles("key-*.xml")).Name;

        await using (var rotated = _hosts.HostOn(_volume, With(_second, previous: _first)))
        {
            var now = DateTimeOffset.UtcNow;
            rotated.Services.GetRequiredService<IKeyManager>().CreateNewKey(now, now + DefaultKeyLifetime);
        }

        var newKey = File.ReadAllText(Assert.Single(_volume.GetFiles("key-*.xml"), file => file.Name != firstKey).FullName);
        Assert.Contains(_second.RawBase64, newKey, StringComparison.Ordinal);
        Assert.DoesNotContain(_first.RawBase64, newKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Retirada antes da hora (docs/operations/deploy.md): sem o certificado que cifrou a chave, a sessão e os links
    /// emitidos com ela falham de forma limpa, 401 e 400, nunca 500.
    /// </summary>
    [Fact]
    public async Task RetiringThePreviousCertificateTooEarlyEndsSessionsAndLinksCleanly()
    {
        var email = IdentityScenario.NewEmail();
        string cookie;
        AccountLink link;
        await using (var before = _hosts.HostOn(_volume, With(_first)))
        {
            cookie = await _hosts.SignInAsync(before);
            using var registering = new Browser(before);
            using var registered = await IdentityScenario.RegisterAsync(registering, email);
            Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
            link = await new IdentityScenario(_postgres, before).CreateLegacyConfirmationProofAsync(email);
        }

        await using var retired = _hosts.HostOn(_volume, With(_second));
        using var session = await GetMeAsync(retired, cookie);
        using var confirming = new Browser(retired);
        await confirming.FetchCsrfTokenAsync();
        using var confirmation = await confirming.PostJsonAsync(
            "/api/v1/auth/confirm-email",
            new { userId = link.UserId, token = link.Token, password = IdentityScenario.ValidPassword });

        await ProblemAssert.IsProblemAsync(session, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await ProblemAssert.IsProblemAsync(confirmation, HttpStatusCode.BadRequest, "INVALID_TOKEN");
    }

    /// <summary>
    /// Chave cifrada por um certificado ausente nunca se decifra: esperar não traz o certificado de volta. O
    /// resolvedor padrão tentaria de novo 10 vezes, com Thread.Sleep de 200 ms antes de cada tentativa, dentro da
    /// trava do KeyRingProvider (na partida a frio e no refresh forçado).
    /// </summary>
    [Fact]
    public async Task KeyFromARetiredCertificateIsSkippedWithoutRetrying()
    {
        await _hosts.SignInOnAFreshHostAsync(_volume, With(_first));
        var retiredKeyId = Path.GetFileNameWithoutExtension(Assert.Single(_volume.GetFiles("key-*.xml")).Name)["key-".Length..];
        var logs = new CapturingLoggerProvider();
        var settings = With(_second);
        settings["Logging:LogLevel:Microsoft.AspNetCore.DataProtection"] = "Debug";
        await using var retired = new DuoraWebFactory(_postgres.ConnectionString, logs)
        {
            SharedKeyRing = _volume,
            Settings = settings,
        };

        using var browser = new Browser(retired);
        await browser.FetchCsrfTokenAsync();

        var resolver = logs.Entries.Where(entry => entry.Category == KeyResolverCategory).ToList();
        Assert.DoesNotContain(resolver, entry => entry.EventId.Id == RetryingKeyAfterFailure);
        var ineligible = resolver.Where(entry => entry.EventId.Id == KeyIneligibleAfterRetries).ToList();
        Assert.Equal(ResolutionsAtColdStartWithoutUsableKey, ineligible.Count);
        Assert.All(ineligible, entry => Assert.Contains(retiredKeyId, entry.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviousCertificatesMayHoldMoreThanOneCertificate()
    {
        var cookie = await _hosts.SignInOnAFreshHostAsync(_volume, With(_first));
        var older = TestCertificates.Create("duora-key-ring-0");

        await using var rotated = _hosts.HostOn(_volume, new Dictionary<string, string?>
        {
            [RequireCertificate] = "true",
            [Certificate] = _second.Pem,
            [PreviousCertificates] = older.Pem + _first.Pem,
        });
        using var response = await GetMeAsync(rotated, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Dictionary<string, string?> With(TestCertificate current, TestCertificate? previous = null) =>
        new()
        {
            [RequireCertificate] = "true",
            [Certificate] = current.Pem,
            [PreviousCertificates] = previous?.Pem ?? "",
        };

    private static async Task<HttpResponseMessage> GetMeAsync(DuoraWebFactory host, string cookie)
    {
        var browser = KeyRingHosts.BrowserWithCookie(host, cookie);
        var response = await browser.GetAsync(KeyRingHosts.MePath);
        await response.Content.LoadIntoBufferAsync(TestContext.Current.CancellationToken);
        browser.Dispose();
        return response;
    }
}
