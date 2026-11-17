using System.Net;
using Duora.IntegrationTests.Identity;
using Microsoft.AspNetCore.Http;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// API-M04 com o adaptador S3 de verdade (#504; docs/api/media-review.md): falha transitória do storage é 503 com
/// Retry-After, a mesma resposta do I/O local; credencial ou bucket errados são defeito de configuração, 500 sem
/// Retry-After. Os testes de leitura do <see cref="MediaAssetReadTests"/> usam o filesystem, que só lança
/// <see cref="IOException"/>, e por isso não viam a diferença.
/// </summary>
public sealed class MediaStorageUnavailableTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly string _canary = Guid.NewGuid().ToString("N");
    private readonly DuoraWebFactory _seed = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _seed.DisposeAsync();

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError")]
    public async Task ATransientS3ErrorOnReadAnswers503WithRetryAfter(HttpStatusCode status, string code)
    {
        await using var fake = await StartS3WithCanaryAsync(FakeS3Server.Error(status, code));

        await ReadApprovedAssetAsync(fake.Url, AssertUnavailableAsync);
    }

    [Fact]
    public async Task AnS3ThatIsUnreachableAnswers503WithRetryAfter() =>
        await ReadApprovedAssetAsync(await FakeS3Server.ClosedUrlAsync(), AssertUnavailableAsync);

    [Fact]
    public async Task AStreamFailureBeforeTheFirstByteAnswers503WithTheStreamRetryAfter()
    {
        await using var fake = await StartS3WithCanaryAsync(FakeS3Server.BodyDropped());

        await ReadApprovedAssetAsync(fake.Url, async response =>
        {
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
            Assert.Equal("2", Assert.Single(response.Headers.GetValues("Retry-After")));
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied")]
    [InlineData(HttpStatusCode.Forbidden, "InvalidAccessKeyId")]
    [InlineData(HttpStatusCode.Forbidden, "SignatureDoesNotMatch")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket")]
    public async Task ACredentialOrBucketErrorOnReadStaysAServerErrorWithoutRetryAfter(
        HttpStatusCode status,
        string code)
    {
        await using var fake = await StartS3WithCanaryAsync(FakeS3Server.Error(status, code));

        await ReadApprovedAssetAsync(fake.Url, async response =>
        {
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
            Assert.False(response.Headers.Contains("Retry-After"));
        });
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.True(ProblemAssert.RetryAfterSeconds(response) > 0);
    }

    private Task<FakeS3Server> StartS3WithCanaryAsync(Func<HttpContext, Task> respond) =>
        FakeS3Server.StartAsync(context =>
        {
            context.Response.Headers["x-amz-meta-duora-canary"] = _canary;
            return respond(context);
        });

    private async Task AssertNoInternalLeakAsync(HttpResponseMessage response, CapturingLoggerProvider logs)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Duora.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Amazon", body, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", body, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, body, StringComparison.OrdinalIgnoreCase);
        var headers = response.Headers.Concat(response.Content.Headers).ToArray();
        Assert.DoesNotContain(headers, header => header.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(headers.SelectMany(header => header.Value),
            value => value.Contains(_canary, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains(_canary, StringComparison.OrdinalIgnoreCase)
            || entry.Fields.Values.Any(value => value?.ToString()?.Contains(_canary, StringComparison.OrdinalIgnoreCase) == true));
    }

    /// <summary>
    /// Semeia a foto aprovada pelo host com filesystem e a lê por um segundo host, ligado ao S3 de mentira, com a
    /// mesma conta e o mesmo banco. A resposta é conferida antes de o host ser descartado.
    /// </summary>
    private async Task ReadApprovedAssetAsync(string s3Url, Func<HttpResponseMessage, Task> assertAsync)
    {
        var media = new MediaScenario(postgres, _seed);
        var account = await media.Identity.CreateConfirmedAccountAsync();
        using var seeding = await media.Identity.SignedInBrowserAsync(account);
        using var upload = await MediaScenario.UploadAsync(seeding, MediaScenario.JpegLike());
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        var asset = await media.ApproveWithVariantsAsync(Assert.Single(await media.UploadsOfAsync(account.UserId)));

        using var logs = new CapturingLoggerProvider();
        await using var s3 = new DuoraWebFactory(postgres.ConnectionString, logs)
        {
            Settings = new Dictionary<string, string?>
            {
                ["Media:Storage:Provider"] = "S3",
                ["Media:Storage:S3:ServiceUrl"] = s3Url,
                ["Media:Storage:S3:Region"] = "us-east-1",
                ["Media:Storage:S3:Bucket"] = $"duora-media-test-{_canary}",
                ["Media:Storage:S3:AccessKeyId"] = $"synthetic-access-{_canary}",
                ["Media:Storage:S3:SecretAccessKey"] = $"synthetic-secret-{_canary}",
                ["Media:Storage:S3:AllowInsecureHttp"] = "true",
            },
        };
        using var browser = await new IdentityScenario(postgres.ConnectionString, s3).SignedInBrowserAsync(account);

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}");
        await assertAsync(response);
        await AssertNoInternalLeakAsync(response, logs);
    }
}
