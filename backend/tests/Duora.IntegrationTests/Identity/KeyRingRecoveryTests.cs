using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// T-15 (spec 11.2): o key ring é estado operacional. Cada teste recria o host web sobre o mesmo banco, como o
/// deploy recria o container; o diretório compartilhado entre as fábricas faz o papel do volume nomeado.
/// </summary>
public sealed class KeyRingRecoveryTests : IDisposable
{

    // Formato do payload do Data Protection: 4 bytes de cabeçalho mágico e, em seguida, o id da chave (16 bytes).
    private const int KeyIdOffset = 4;
    private const int KeyIdLength = 16;

    private static readonly TimeSpan DefaultKeyLifetime = TimeSpan.FromDays(90);

    private readonly PostgresFixture _postgres;
    private readonly KeyRingHosts _hosts;
    private readonly DirectoryInfo _volume = Directory.CreateTempSubdirectory("duora-keys-volume-");

    public KeyRingRecoveryTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _hosts = new KeyRingHosts(postgres);
    }

    public void Dispose() => _volume.Delete(recursive: true);

    [Fact]
    public async Task SessionIssuedBeforeTheHostIsRecreatedStillAuthenticatesWithTheSameKeyRing()
    {
        var cookie = await _hosts.SignInOnAFreshHostAsync(_volume);

        await using var recreated = _hosts.HostOn(_volume);
        using var browser = KeyRingHosts.BrowserWithCookie(recreated, cookie);
        using var response = await browser.GetAsync(KeyRingHosts.MePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RotatedKeyRingKeepsThePreviousKeyToReadExistingSessions()
    {
        Guid previousKey;
        string cookie;
        await using (var before = _hosts.HostOn(_volume))
        {
            cookie = await _hosts.SignInAsync(before);
            previousKey = ProtectingKey(before);
            var now = DateTimeOffset.UtcNow;
            before.Services.GetRequiredService<IKeyManager>().CreateNewKey(now, now + DefaultKeyLifetime);
        }

        await using var rotated = _hosts.HostOn(_volume);
        using var browser = KeyRingHosts.BrowserWithCookie(rotated, cookie);
        using var response = await browser.GetAsync(KeyRingHosts.MePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(previousKey, ProtectingKey(rotated));
        Assert.Contains(
            rotated.Services.GetRequiredService<IKeyManager>().GetAllKeys(),
            key => key.KeyId == previousKey && !key.IsRevoked);
    }

    [Fact]
    public async Task EmptyKeyRingEndsTheSessionWithUnauthorizedNotServerError()
    {
        var cookie = await _hosts.SignInOnAFreshHostAsync(_volume);
        var emptyVolume = Directory.CreateTempSubdirectory("duora-keys-empty-");

        try
        {
            await using var recreated = _hosts.HostOn(emptyVolume);
            using var browser = KeyRingHosts.BrowserWithCookie(recreated, cookie);
            using var response = await browser.GetAsync(KeyRingHosts.MePath);

            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        }
        finally
        {
            emptyVolume.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A revogação vale para o host que sobe depois dela: num processo vivo o KeyRingProvider ainda serve o key ring
    /// em cache até recarregá-lo, então o teste revoga num host e confere no seguinte (docs/security/auth.md).
    /// </summary>
    [Fact]
    public async Task RevokedKeyRingEndsTheSessionWithUnauthorizedNotServerError()
    {
        var cookie = await _hosts.SignInOnAFreshHostAsync(_volume);
        await using (var compromised = _hosts.HostOn(_volume))
        {
            compromised.Services.GetRequiredService<IKeyManager>()
                .RevokeAllKeys(DateTimeOffset.UtcNow, "T-15: resposta a comprometimento do key ring");
        }

        await using var recreated = _hosts.HostOn(_volume);
        using var browser = KeyRingHosts.BrowserWithCookie(recreated, cookie);
        using var response = await browser.GetAsync(KeyRingHosts.MePath);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    /// <summary>
    /// Cookie legível e ticket do banco ilegível (chave que protegeu o ticket perdida, ou linha adulterada): o ticket
    /// store trata a sessão como inexistente, responde 401 e registra o aviso, sem 500.
    /// </summary>
    [Fact]
    public async Task UnreadableTicketInTheDatabaseEndsTheSessionWithUnauthorizedAndAWarning()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = new DuoraWebFactory(_postgres.ConnectionString, logs) { SharedKeyRing = _volume };
        var scenario = new IdentityScenario(_postgres, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);

        var tampered = await scenario.ScalarAsync(
            "with changed as (update identity.login_sessions set ticket = $2 where user_id = $1 and revoked_at is null "
            + "returning 1) select count(*) from changed",
            account.UserId,
            new byte[] { 0x09, 0xF0, 0xC9, 0xF0, 0x01, 0x02, 0x03 });
        using var response = await browser.GetAsync(KeyRingHosts.MePath);

        Assert.Equal(1, tampered);
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Text.Contains("Ticket de sessão ilegível", StringComparison.Ordinal)
            && entry.Exception is CryptographicException);
    }

    /// <summary>A política de rotação depende destes dois valores (docs/security/auth.md, Data Protection).</summary>
    [Fact]
    public async Task KeysLiveNinetyDaysUnderTheStableApplicationName()
    {
        await using var host = _hosts.HostOn(_volume);

        Assert.Equal(DefaultKeyLifetime, host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.NewKeyLifetime);
        Assert.Equal("Duora", host.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
    }

    [Fact]
    public async Task NewLoginWorksAfterTheKeyRingIsLost()
    {
        await _hosts.SignInOnAFreshHostAsync(_volume);
        var emptyVolume = Directory.CreateTempSubdirectory("duora-keys-empty-");

        try
        {
            await using var recreated = _hosts.HostOn(emptyVolume);
            var cookie = await _hosts.SignInAsync(recreated);
            using var browser = KeyRingHosts.BrowserWithCookie(recreated, cookie);
            using var response = await browser.GetAsync(KeyRingHosts.MePath);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            emptyVolume.Delete(recursive: true);
        }
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
