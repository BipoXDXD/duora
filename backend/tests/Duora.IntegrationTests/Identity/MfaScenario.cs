using System.Net;
using System.Text.Json.Nodes;

namespace Duora.IntegrationTests.Identity;

/// <summary>Fator TOTP ativo numa conta de teste: a chave (do "autenticador") e os códigos de recuperação.</summary>
internal sealed record EnabledMfa(string SharedKey, IReadOnlyList<string> RecoveryCodes);

/// <summary>Passos de MFA pela API (API-A11 a A15), como a SPA os faria.</summary>
internal static class MfaScenario
{
    public const string Enrollments = "/api/v1/me/mfa/enrollments";
    public const string Confirmations = "/api/v1/me/mfa/confirmations";
    public const string RecoveryCodes = "/api/v1/me/mfa/recovery-codes";
    public const string Mfa = "/api/v1/me/mfa";
    public const string Reauthenticate = "/api/v1/auth/reauthenticate";

    public static async Task<string> StartEnrollmentAsync(Browser browser)
    {
        using var response = await browser.PostJsonAsync(Enrollments, body: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadObjectAsync(response);
        return (string?)body["sharedKey"] ?? throw new InvalidOperationException("Sem sharedKey.");
    }

    public static async Task<EnabledMfa> EnableAsync(Browser browser)
    {
        var sharedKey = await StartEnrollmentAsync(browser);
        using var confirmed = await browser.PostJsonAsync(Confirmations, new { code = Totp.Now(sharedKey) });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var codes = (await ReadObjectAsync(confirmed))["recoveryCodes"]!.AsArray()
            .Select(code => (string?)code ?? "")
            .ToList();
        await browser.FetchCsrfTokenAsync();
        return new EnabledMfa(sharedKey, codes);
    }

    public static async Task<HttpResponseMessage> LoginAsync(
        Browser browser,
        TestAccount account,
        string? twoFactorCode = null,
        string? recoveryCode = null)
    {
        if (browser.Cookie("XSRF-TOKEN") is null)
        {
            await browser.FetchCsrfTokenAsync();
        }

        return await browser.PostJsonAsync(
            "/api/v1/auth/login",
            new { email = account.Email, password = account.Password, twoFactorCode, recoveryCode });
    }

    public static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))?.AsObject()
        ?? throw new InvalidOperationException("Corpo não é objeto.");
}
