using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// T-15 (spec 11.2): o key ring é estado operacional. Cada teste recria o host web sobre o mesmo banco, como o
/// deploy recria o container; o diretório compartilhado entre as fábricas faz o papel do volume nomeado.
/// </summary>
public sealed class KeyRingRecoveryTests : IDisposable
{
    private const string SessionCookie = "__Host-duora";
    private const string MePath = "/api/v1/me";

    // Formato do payload do Data Protection: 4 bytes de cabeçalho mágico e, em seguida, o id da chave (16 bytes).
    private const int KeyIdOffset = 4;
    private const int KeyIdLength = 16;

    private static readonly TimeSpan DefaultKeyLifetime = TimeSpan.FromDays(90);

    private readonly PostgresFixture _postgres;
    private readonly DirectoryInfo _volume = Directory.CreateTempSubdirectory("duora-keys-volume-");

    public KeyRingRecoveryTests(PostgresFixture postgres) => _postgres = postgres;

    public void Dispose() => _volume.Delete(recursive: true);

    [Fact]
    public async Task SessionIssuedBeforeTheHostIsRecreatedStillAuthenticatesWithTheSameKeyRing()
    {
        var cookie = await SignInOnAFreshHostAsync(_volume);

        await using var recreated = HostOn(_volume);
        using var browser = BrowserWithCookie(recreated, cookie);
        using var response = await browser.GetAsync(MePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RotatedKeyRingKeepsThePreviousKeyToReadExistingSessions()
    {
        Guid previousKey;
        string cookie;
        await using (var before = HostOn(_volume))
        {
            cookie = await SignInAsync(before);
            previousKey = ProtectingKey(before);
            var now = DateTimeOffset.UtcNow;
            before.Services.GetRequiredService<IKeyManager>().CreateNewKey(now, now + DefaultKeyLifetime);
        }

        await using var rotated = HostOn(_volume);
        using var browser = BrowserWithCookie(rotated, cookie);
        using var response = await browser.GetAsync(MePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(previousKey, ProtectingKey(rotated));
        Assert.Contains(
            rotated.Services.GetRequiredService<IKeyManager>().GetAllKeys(),
            key => key.KeyId == previousKey && !key.IsRevoked);
    }

    [Fact]
    public async Task EmptyKeyRingEndsTheSessionWithUnauthorizedNotServerError()
    {
        var cookie = await SignInOnAFreshHostAsync(_volume);
        var emptyVolume = Directory.CreateTempSubdirectory("duora-keys-empty-");

        try
        {
            await using var recreated = HostOn(emptyVolume);
            using var browser = BrowserWithCookie(recreated, cookie);
            using var response = await browser.GetAsync(MePath);

            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        }
        finally
        {
            emptyVolume.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RevokedKeyRingEndsTheSessionWithUnauthorizedNotServerError()
    {
        var cookie = await SignInOnAFreshHostAsync(_volume);

        await using var compromised = HostOn(_volume);
        compromised.Services.GetRequiredService<IKeyManager>()
            .RevokeAllKeys(DateTimeOffset.UtcNow, "T-15: resposta a comprometimento do key ring");
        using var browser = BrowserWithCookie(compromised, cookie);
        using var response = await browser.GetAsync(MePath);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task NewLoginWorksAfterTheKeyRingIsLost()
    {
        await SignInOnAFreshHostAsync(_volume);
        var emptyVolume = Directory.CreateTempSubdirectory("duora-keys-empty-");

        try
        {
            await using var recreated = HostOn(emptyVolume);
            var cookie = await SignInAsync(recreated);
            using var browser = BrowserWithCookie(recreated, cookie);
            using var response = await browser.GetAsync(MePath);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            emptyVolume.Delete(recursive: true);
        }
    }

    private DuoraWebFactory HostOn(DirectoryInfo keyRing) =>
        new(_postgres.ConnectionString) { SharedKeyRing = keyRing };

    private async Task<string> SignInOnAFreshHostAsync(DirectoryInfo keyRing)
    {
        await using var host = HostOn(keyRing);
        return await SignInAsync(host);
    }

    private async Task<string> SignInAsync(DuoraWebFactory host)
    {
        var scenario = new IdentityScenario(_postgres, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        return browser.Cookie(SessionCookie) ?? throw new InvalidOperationException("Login sem cookie de sessão.");
    }

    private static Browser BrowserWithCookie(DuoraWebFactory host, string cookie)
    {
        var browser = new Browser(host);
        browser.Cookies.Add(Browser.BaseAddress, new Cookie(SessionCookie, cookie));
        return browser;
    }

    /// <summary>Chave padrão do host: a que ele usa para proteger payloads novos.</summary>
    private static Guid ProtectingKey(DuoraWebFactory host)
    {
        var payload = host.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Duora.Tests.KeyRingRecovery")
            .Protect([1]);
        return new Guid(payload.AsSpan(KeyIdOffset, KeyIdLength));
    }
}
