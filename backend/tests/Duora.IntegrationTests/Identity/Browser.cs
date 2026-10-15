using System.Net;
using System.Net.Http.Json;
using System.Text;
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

    public Browser(DuoraWebFactory factory)
    {
        _http = factory.CreateDefaultClient(BaseAddress, new CookieContainerHandler(Cookies));
    }

    public CookieContainer Cookies { get; } = new();

    public string? Cookie(string name) => Cookies.GetCookies(BaseAddress)[name]?.Value;

    public async Task FetchCsrfTokenAsync()
    {
        using var response = await GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public Task<HttpResponseMessage> GetAsync(string path) =>
        _http.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> PostJsonAsync(string path, object? body, MutationHeaders? headers = null) =>
        SendAsync(HttpMethod.Post, path, body is null ? null : JsonContent.Create(body), headers);

    /// <summary>Corpo cru, para JSON malformado ou com campo extra.</summary>
    public Task<HttpResponseMessage> PostRawJsonAsync(string path, string json, MutationHeaders? headers = null) =>
        SendAsync(HttpMethod.Post, path, new StringContent(json, Encoding.UTF8, "application/json"), headers);

    public Task<HttpResponseMessage> DeleteAsync(string path, MutationHeaders? headers = null) =>
        SendAsync(HttpMethod.Delete, path, content: null, headers);

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        MutationHeaders? headers)
    {
        headers ??= MutationHeaders.SameOrigin;
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        if (headers.SendCsrfToken && Cookie("XSRF-TOKEN") is { } token)
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

        foreach (var (name, value) in headers.Extra ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        return await _http.SendAsync(request, TestContext.Current.CancellationToken);
    }
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
