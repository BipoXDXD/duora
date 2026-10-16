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

    private static readonly TimeSpan DefaultKeyLifetime = TimeSpan.FromDays(90);

    private readonly KeyRingHosts _hosts;
    private readonly DirectoryInfo _volume = Directory.CreateTempSubdirectory("duora-keys-volume-");
    private readonly TestCertificate _first = TestCertificates.Create("duora-key-ring-1");
    private readonly TestCertificate _second = TestCertificates.Create("duora-key-ring-2");

    public KeyRingCertificateTests(PostgresFixture postgres) => _hosts = new KeyRingHosts(postgres);

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
