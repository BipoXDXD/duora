using System.Net;
using System.Net.Http.Headers;

namespace Duora.Infrastructure.Geolocation;

public sealed record CountryDatabaseDownloadOptions(long MaxBytes, TimeSpan Timeout, IReadOnlySet<string> RedirectHosts);
public sealed class CountryDatabaseUpdateException(string code, bool transient, DateTimeOffset? retryAfter = null)
    : Exception("Country database update failed.")
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
    public DateTimeOffset? RetryAfter { get; } = retryAfter;
}

/// <summary>Private client without factory logging; no automatic redirect, cookie or decompression.</summary>
public sealed class CountryDatabaseDownloader : IDisposable
{
    private static readonly Uri Source = new("https://ipinfo.io/data/free/country.mmdb");
    private readonly HttpClient _client;
    private readonly CountryDatabaseDownloadOptions _options;
    private readonly string _token;
    private readonly TimeProvider _time;
    private readonly Func<string, Stream> _createFile;

    public static CountryDatabaseDownloader Create(CountryDatabaseDownloadOptions options, string token, TimeProvider time) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None }, options, token, time);

    internal CountryDatabaseDownloader(HttpMessageHandler handler, CountryDatabaseDownloadOptions options, string token, TimeProvider time,
        Func<string, Stream>? createFile = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Timeout, TimeSpan.Zero);
        _client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _options = options; _token = token; _time = time;
        _createFile = createFile ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous));
    }

    public async Task<DateTimeOffset> DownloadAsync(string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(_options.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var uri = Source;
            for (var hop = 0; hop <= 3; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                if (hop == 0) { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token); }
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    if (hop == 3 || location is null) { throw new CountryDatabaseUpdateException("Validation", false); }
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (next.Scheme != Uri.UriSchemeHttps || next.Port != 443 || next.UserInfo.Length != 0 || next.Fragment.Length != 0
                        || !_options.RedirectHosts.Contains(next.IdnHost))
                    {
                        throw new CountryDatabaseUpdateException("Validation", false);
                    }
                    uri = next; continue;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new CountryDatabaseUpdateException("Unauthorized", false);
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter;
                    DateTimeOffset? at = retry?.Date;
                    if (retry?.Delta is { } delta)
                    {
                        try { at = _time.GetUtcNow() + delta; }
                        catch (ArgumentOutOfRangeException) { throw new CountryDatabaseUpdateException("Validation", false); }
                    }
                    throw new CountryDatabaseUpdateException("RateLimited", true, at);
                }
                if ((int)response.StatusCode >= 500) { throw new CountryDatabaseUpdateException("ServerError", true); }
                if (response.StatusCode == HttpStatusCode.RequestTimeout) { throw new CountryDatabaseUpdateException("Timeout", true); }
                if (response.StatusCode != HttpStatusCode.OK) { throw new CountryDatabaseUpdateException("Validation", false); }
                if (response.Content.Headers.ContentLength > _options.MaxBytes || response.Content.Headers.ContentEncoding.Count != 0)
                {
                    throw new CountryDatabaseUpdateException("Integrity", false);
                }
                await using var input = await response.Content.ReadAsStreamAsync(linked.Token);
                await using var output = _createFile(destination);
                var buffer = new byte[65536]; long size = 0; int count;
                while ((count = await input.ReadAsync(buffer, linked.Token)) != 0)
                {
                    if (count > _options.MaxBytes - size) { throw new CountryDatabaseUpdateException("Integrity", false); }
                    size += count;
                    await output.WriteAsync(buffer.AsMemory(0, count), linked.Token);
                }
                if (response.Content.Headers.ContentLength is { } length && size != length)
                {
                    throw new CountryDatabaseUpdateException("Integrity", false);
                }
                await output.FlushAsync(linked.Token);
                if (output is FileStream file) { file.Flush(flushToDisk: true); }
                return _time.GetUtcNow();
            }
            throw new CountryDatabaseUpdateException("Validation", false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CountryDatabaseUpdateException("Timeout", true); }
        catch (HttpRequestException) { throw new CountryDatabaseUpdateException("Network", true); }
        catch (IOException) { throw new CountryDatabaseUpdateException("Disk", true); }
        catch (UnauthorizedAccessException) { throw new CountryDatabaseUpdateException("Disk", false); }
    }

    public void Dispose() { _client.Dispose(); GC.SuppressFinalize(this); }
}
