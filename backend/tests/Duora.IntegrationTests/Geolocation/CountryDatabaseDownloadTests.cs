using System.Net;
using System.Net.Http.Headers;
using Duora.Infrastructure.Geolocation;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Geolocation;

public sealed class CountryDatabaseDownloadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InitialRequestUsesLiteOriginWithoutQueryCredentials()
    {
        using var h = new Harness();
        using var downloader = h.Downloader((request, _) =>
        {
            Assert.Equal("https://ipinfo.io/data/ipinfo_lite.mmdb", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        });
        await downloader.DownloadAsync(h.Path, Ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(h.Path, Ct));
    }

    [Fact]
    public async Task RedirectRequiresAllowlistAndNeverForwardsAuthorization()
    {
        using var h = new Harness();
        var requests = new List<(string Host, bool Auth)>();
        using var downloader = h.Downloader((r, _) =>
        {
            requests.Add((r.RequestUri!.Host, r.Headers.Authorization is not null));
            if (requests.Count == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://downloads.example.invalid/signed?opaque=synthetic");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        });
        Assert.Equal(Now, await downloader.DownloadAsync(h.Path, Ct));
        Assert.Equal(("ipinfo.io", true), requests[0]);
        Assert.Equal(("downloads.example.invalid", false), requests[1]);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(h.Path, Ct));
    }

    [Theory]
    [InlineData("http://downloads.example.invalid/file")]
    [InlineData("https://other.example.invalid/file")]
    [InlineData("https://user:secret@downloads.example.invalid/file")]
    [InlineData("https://downloads.example.invalid:444/file")]
    public async Task UnsafeRedirectIsRejectedBeforeAnySecondRequest(string location)
    {
        using var h = new Harness();
        var calls = 0;
        using var downloader = h.Downloader((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<CountryDatabaseUpdateException>(() => downloader.DownloadAsync(h.Path, Ct));
        Assert.False(error.Transient);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, false, "Unauthorized")]
    [InlineData(403, false, "Unauthorized")]
    [InlineData(429, true, "RateLimited")]
    [InlineData(503, true, "ServerError")]
    public async Task FailuresAreCategorizedWithoutLeakingProviderPayload(int status, bool transient, string code)
    {
        using var h = new Harness();
        using var downloader = h.Downloader((_, _) =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("synthetic-sensitive-canary") };
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
            return Task.FromResult(r);
        });
        var error = await Assert.ThrowsAsync<CountryDatabaseUpdateException>(() => downloader.DownloadAsync(h.Path, Ct));
        Assert.Equal(transient, error.Transient);
        Assert.Equal(code, error.Code);
        if (status is 429 or 503)
        { Assert.Equal(Now.AddMinutes(30), error.RetryAfter); }
        Assert.DoesNotContain("synthetic-sensitive-canary", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ByteLimitAppliesWithoutContentLengthAndDiskFullIsBoundedFailure()
    {
        using var h = new Harness();
        using (var downloader = h.Downloader((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new UnknownLengthStream(new byte[11])) }), maxBytes: 10))
        {
            Assert.Equal("Integrity", (await Assert.ThrowsAsync<CountryDatabaseUpdateException>(() => downloader.DownloadAsync(h.Path, Ct))).Code);
        }
        if (File.Exists(h.Path))
        { File.Delete(h.Path); }
        using (var downloader = h.Downloader((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1]) }), createFile: _ => new FullDisk()))
        {
            Assert.Equal("Disk", (await Assert.ThrowsAsync<CountryDatabaseUpdateException>(() => downloader.DownloadAsync(h.Path, Ct))).Code);
        }
    }

    [Fact]
    public async Task TimeoutCancelsInFlightRequestAndSanitizesNetworkExceptions()
    {
        using var h = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var downloader = h.Downloader(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(HttpStatusCode.OK);
        }))
        {
            var work = downloader.DownloadAsync(h.Path, Ct);
            await started.Task.WaitAsync(Ct);
            h.Time.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal("Timeout", (await Assert.ThrowsAsync<CountryDatabaseUpdateException>(() => work)).Code);
        }
        using var broken = h.Downloader((_, _) => throw new HttpRequestException("synthetic-token-canary"));
        var error = await Assert.ThrowsAsync<CountryDatabaseUpdateException>(() => broken.DownloadAsync(h.Path, Ct));
        Assert.Equal("Network", error.Code);
        Assert.DoesNotContain("synthetic-token-canary", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectLoopStopsAtTheBoundAndNeverWritesAFile()
    {
        using var h = new Harness();
        var calls = 0;
        using var downloader = h.Downloader((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://downloads.example.invalid/loop");
            return Task.FromResult(response);
        });
        Assert.Equal("Validation", (await Assert.ThrowsAsync<CountryDatabaseUpdateException>(() => downloader.DownloadAsync(h.Path, Ct))).Code);
        Assert.Equal(4, calls);
        Assert.False(File.Exists(h.Path));
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "duora-download-" + Guid.NewGuid().ToString("N"));
        public FakeTimeProvider Time { get; } = new(Now);
        public string Path => System.IO.Path.Combine(_root, "candidate.mmdb");
        public Harness() => Directory.CreateDirectory(_root);
        public CountryDatabaseDownloader Downloader(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, long maxBytes = 1000,
            Func<string, Stream>? createFile = null) => new(new Handler(send),
                new(maxBytes, TimeSpan.FromSeconds(30), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "downloads.example.invalid" }), "synthetic-token", Time, createFile);
        public void Dispose() => Directory.Delete(_root, true);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class FullDisk : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Synthetic ENOSPC."));
    }
}
