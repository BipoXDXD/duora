using System.Net;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Hosts web recriados sobre o mesmo banco, cada um com o key ring e a configuração que o teste der (T-15): o
/// diretório compartilhado entre as fábricas faz o papel do volume nomeado.
/// </summary>
internal sealed class KeyRingHosts(PostgresFixture postgres)
{
    public const string SessionCookie = "__Host-duora";
    public const string MePath = "/api/v1/me";

    public DuoraWebFactory HostOn(DirectoryInfo keyRing, IReadOnlyDictionary<string, string?>? settings = null) =>
        new(postgres.ConnectionString)
        {
            SharedKeyRing = keyRing,
            Settings = settings ?? new Dictionary<string, string?>(),
        };

    public async Task<string> SignInOnAFreshHostAsync(
        DirectoryInfo keyRing,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        await using var host = HostOn(keyRing, settings);
        return await SignInAsync(host);
    }

    public async Task<string> SignInAsync(DuoraWebFactory host)
    {
        var scenario = new IdentityScenario(postgres, host);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        return browser.Cookie(SessionCookie) ?? throw new InvalidOperationException("Login sem cookie de sessão.");
    }

    public static Browser BrowserWithCookie(DuoraWebFactory host, string cookie)
    {
        var browser = new Browser(host);
        browser.Cookies.Add(Browser.BaseAddress, new Cookie(SessionCookie, cookie));
        return browser;
    }
}
