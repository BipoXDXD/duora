using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Um navegador da SPA: pote de cookies próprio, https, sem seguir redirect e, por padrão, com os headers que um
/// navegador manda numa chamada same-origin (Origin, Sec-Fetch-Site e o X-XSRF-TOKEN lido do cookie).
/// </summary>
internal sealed class Browser : IDisposable
{
    public static readonly Uri BaseAddress = new(DuoraWebFactory.PublicOrigin);

    private readonly HttpClient _http;
    private string? _privateProof;

    public Browser(DuoraWebFactory factory)
    {
        _http = factory.CreateDefaultClient(BaseAddress, new CookieContainerHandler(Cookies));
    }

    internal Browser(HttpClient http, CookieContainer cookies)
    {
        _http = http;
        Cookies = cookies;
    }

    public CookieContainer Cookies { get; } = new();

    public string? Cookie(string name) => Cookies.GetCookies(_http.BaseAddress ?? BaseAddress)[name]?.Value;

    public async Task FetchCsrfTokenAsync()
    {
        using var response = await GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public async Task FetchPrivateContextAsync()
    {
        using var response = await GetAsync("/api/v1/me");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden);
        Assert.NotNull(_privateProof);
    }

    public async Task<HttpResponseMessage> GetAsync(string path, string? privateProofOverride = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (NeedsPrivateProof(path) && (privateProofOverride ?? _privateProof) is { } proof)
        {
            request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", proof);
        }
        var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        if (path == "/api/v1/me")
        {
            // Apenas observa o bootstrap que o teste pediu. Nunca reconsulta para consertar uma prova velha.
            _privateProof = null;
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken);
                if (response.StatusCode == HttpStatusCode.OK
                    || (body?["code"]?.GetValue<string>() == "PASSWORD_CHANGE_REQUIRED"
                        && body?["sessionContext"]?["phase"]?.GetValue<string>() == "password-change"))
                {
                    _privateProof = body?["sessionContext"]?["requestToken"]?.GetValue<string>();
                }
            }
        }
        return response;
    }

    public Task<HttpResponseMessage> GetWithoutContextAsync(string path) =>
        _http.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> PostJsonAsync(string path, object? body, MutationHeaders? headers = null) =>
        SendAsync(HttpMethod.Post, path, body is null ? null : JsonContent.Create(body), headers);

    public Task<HttpResponseMessage> PutJsonAsync(string path, object body, MutationHeaders? headers = null) =>
        SendAsync(HttpMethod.Put, path, JsonContent.Create(body), headers);

    /// <summary>Corpo cru, para JSON malformado ou com campo extra.</summary>
    public Task<HttpResponseMessage> PostRawJsonAsync(string path, string json, MutationHeaders? headers = null) =>
        SendAsync(HttpMethod.Post, path, new StringContent(json, Encoding.UTF8, "application/json"), headers);

    /// <summary>PUT sem corpo, ou com JSON cru quando o teste quer mandar campos que a rota não aceita.</summary>
    public Task<HttpResponseMessage> PutAsync(string path, string? rawJson = null, MutationHeaders? headers = null) =>
        SendAsync(
            HttpMethod.Put,
            path,
            rawJson is null ? null : new StringContent(rawJson, Encoding.UTF8, "application/json"),
            headers);

    /// <summary>Corpo qualquer (upload de foto em corpo cru), com o Content-Type e o tamanho do próprio conteúdo.</summary>
    public Task<HttpResponseMessage> PostContentAsync(string path, HttpContent content, MutationHeaders? headers = null) =>
        SendAsync(HttpMethod.Post, path, content, headers);

    public Task<HttpResponseMessage> DeleteAsync(string path, MutationHeaders? headers = null) =>
        SendAsync(HttpMethod.Delete, path, content: null, headers);

    /// <summary>PATCH com corpo cru e <c>If-Match</c> opcional, mandado sem validação para testar valores malformados.</summary>
    public Task<HttpResponseMessage> PatchRawJsonAsync(
        string path,
        string json,
        string? ifMatch,
        MutationHeaders? headers = null) =>
        SendAsync(HttpMethod.Patch, path, new StringContent(json, Encoding.UTF8, "application/json"), headers, ifMatch);

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        MutationHeaders? headers,
        string? ifMatch = null)
    {
        headers ??= MutationHeaders.SameOrigin;
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        var token = NeedsPrivateProof(path) ? _privateProof : Cookie("XSRF-TOKEN");
        if (headers.SendCsrfToken && token is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", headers.CsrfTokenOverride ?? token);
        }
        else if (headers.CsrfTokenOverride is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", headers.CsrfTokenOverride);
        }

        if (headers.Origin is not null)
        {
            request.Headers.Add("Origin", headers.Origin);
        }

        if (headers.FetchSite is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", headers.FetchSite);
        }

        // Sem validação: os testes também mandam headers malformados, como um cliente qualquer mandaria.
        foreach (var (name, value) in headers.Extra ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await _http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private bool NeedsPrivateProof(string path) => path.StartsWith("/api/v1/", StringComparison.Ordinal)
        && path is not ("/api/v1/me" or "/api/v1/auth/csrf" or "/api/v1/auth/register"
            or "/api/v1/auth/confirm-email" or "/api/v1/auth/email-confirmation-requests"
            or "/api/v1/auth/login" or "/api/v1/auth/password-reset-requests" or "/api/v1/auth/password-resets")
        && (path != "/api/v1/auth/logout" || Cookie("__Host-duora") is not null);
}

internal sealed record MutationHeaders(
    bool SendCsrfToken = true,
    string? CsrfTokenOverride = null,
    string? Origin = DuoraWebFactory.PublicOrigin,
    string? FetchSite = "same-origin",
    IReadOnlyDictionary<string, string>? Extra = null)
{
    public static MutationHeaders SameOrigin { get; } = new();
}
