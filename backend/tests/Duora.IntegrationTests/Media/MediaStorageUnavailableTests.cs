using System.Net;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// API-M04 com o adaptador S3 de verdade (#504; docs/api/media-review.md): falha transitória do storage é 503 com
/// Retry-After, a mesma resposta do I/O local; credencial ou bucket errados são defeito de configuração, 500 sem
/// Retry-After. Os testes de leitura do <see cref="MediaAssetReadTests"/> usam o filesystem, que só lança
/// <see cref="IOException"/>, e por isso não viam a diferença.
/// </summary>
public sealed class MediaStorageUnavailableTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly DuoraWebFactory _seed = new(postgres.ConnectionString);

    public ValueTask DisposeAsync() => _seed.DisposeAsync();

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError")]
    public async Task ATransientS3ErrorOnReadAnswers503WithRetryAfter(HttpStatusCode status, string code)
    {
        await using var fake = await FakeS3Server.StartAsync(FakeS3Server.Error(status, code));

        await ReadApprovedAssetAsync(fake.Url, AssertUnavailableAsync);
    }

    [Fact]
    public async Task AnS3ThatIsUnreachableAnswers503WithRetryAfter() =>
        await ReadApprovedAssetAsync(await FakeS3Server.ClosedUrlAsync(), AssertUnavailableAsync);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied")]
    [InlineData(HttpStatusCode.Forbidden, "InvalidAccessKeyId")]
    [InlineData(HttpStatusCode.Forbidden, "SignatureDoesNotMatch")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket")]
    public async Task ACredentialOrBucketErrorOnReadStaysAServerErrorWithoutRetryAfter(HttpStatusCode status, string code)
    {
        await using var fake = await FakeS3Server.StartAsync(FakeS3Server.Error(status, code));

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

        await using var s3 = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?>
            {
                ["Media:Storage:Provider"] = "S3",
                ["Media:Storage:S3:ServiceUrl"] = s3Url,
                ["Media:Storage:S3:Region"] = "us-east-1",
                ["Media:Storage:S3:Bucket"] = "duora-media-test",
                ["Media:Storage:S3:AccessKeyId"] = "synthetic-access",
                ["Media:Storage:S3:SecretAccessKey"] = "synthetic-secret",
                ["Media:Storage:S3:AllowInsecureHttp"] = "true",
            },
        };
        using var browser = await new IdentityScenario(postgres.ConnectionString, s3).SignedInBrowserAsync(account);

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}");
        await assertAsync(response);
    }
}
