using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.Logging;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Classificação das falhas do S3 no adaptador (#504). Falha transitória do storage (5xx, <c>SlowDown</c>, rede,
/// timeout) sobe como <see cref="StorageUnavailableException"/>, um <see cref="IOException"/> que os gates já
/// convertem em 503 com Retry-After; credencial e bucket errados continuam <see cref="AmazonS3Exception"/>, que vira
/// 500 e alerta.
/// </summary>
public sealed class S3ObjectStoreTransientFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, HttpStatusCode, string> TransientErrors => new()
    {
        { "ServiceUnavailable", HttpStatusCode.ServiceUnavailable, "ServiceUnavailable" },
        { "SlowDown", HttpStatusCode.ServiceUnavailable, "SlowDown" },
        { "InternalError", HttpStatusCode.InternalServerError, "InternalError" },
        { "BadGateway", HttpStatusCode.BadGateway, "BadGateway" },
        { "GatewayTimeout", HttpStatusCode.GatewayTimeout, "GatewayTimeout" },
        { "TooManyRequests", HttpStatusCode.TooManyRequests, "TooManyRequests" },
        { "RequestTimeout", HttpStatusCode.BadRequest, "RequestTimeout" },
    };

    public static TheoryData<string, HttpStatusCode, string> ConfigurationErrors => new()
    {
        { "AccessDenied", HttpStatusCode.Forbidden, "AccessDenied" },
        { "InvalidAccessKeyId", HttpStatusCode.Forbidden, "InvalidAccessKeyId" },
        { "SignatureDoesNotMatch", HttpStatusCode.Forbidden, "SignatureDoesNotMatch" },
        { "NoSuchBucket", HttpStatusCode.NotFound, "NoSuchBucket" },
    };

    [Theory]
    [MemberData(nameof(TransientErrors))]
    public async Task ATransientStorageErrorOnReadIsAStorageUnavailableError(string name, HttpStatusCode status, string code)
    {
        _ = name;
        await using var fake = await FakeS3Server.StartAsync(FakeS3Server.Error(status, code));
        using var store = Store(fake.Url);

        var error = await Assert.ThrowsAsync<StorageUnavailableException>(() => store.OpenReadAsync(ObjectKey.NewQuarantine(), Ct));

        Assert.IsType<AmazonServiceException>(error.InnerException, exactMatch: false);
    }

    [Theory]
    [MemberData(nameof(TransientErrors))]
    public async Task ATransientStorageErrorOnWriteIsAStorageUnavailableError(string name, HttpStatusCode status, string code)
    {
        _ = name;
        await using var fake = await FakeS3Server.StartAsync(FakeS3Server.Error(status, code));
        using var store = Store(fake.Url);

        await Assert.ThrowsAsync<StorageUnavailableException>(() =>
            store.PutAsync(ObjectKey.NewQuarantine(), new MemoryStream([1, 2, 3]), 3, "image/png", Ct));
    }

    [Theory]
    [MemberData(nameof(TransientErrors))]
    public async Task ATransientStorageErrorOnDeleteIsAStorageUnavailableError(string name, HttpStatusCode status, string code)
    {
        _ = name;
        await using var fake = await FakeS3Server.StartAsync(FakeS3Server.Error(status, code));
        using var store = Store(fake.Url);

        await Assert.ThrowsAsync<StorageUnavailableException>(() => store.DeleteAsync(ObjectKey.NewQuarantine(), Ct));
    }

    [Theory]
    [MemberData(nameof(ConfigurationErrors))]
    public async Task ACredentialOrBucketErrorStaysAnS3ErrorNotAStorageUnavailableError(string name, HttpStatusCode status, string code)
    {
        _ = name;
        await using var fake = await FakeS3Server.StartAsync(FakeS3Server.Error(status, code));
        using var store = Store(fake.Url);
        var key = ObjectKey.NewQuarantine();

        var read = await Record.ExceptionAsync(() => store.OpenReadAsync(key, Ct));
        var write = await Record.ExceptionAsync(() => store.PutAsync(key, new MemoryStream([1, 2, 3]), 3, "image/png", Ct));
        var delete = await Record.ExceptionAsync(() => store.DeleteAsync(key, Ct));

        Assert.All([read, write, delete], error =>
        {
            Assert.IsType<AmazonS3Exception>(error);
            Assert.Equal(code, ((AmazonS3Exception)error).ErrorCode);
        });
    }

    [Fact]
    public async Task AMissingObjectStillReadsAsNullAndADeleteOfItStillSucceeds()
    {
        await using var fake = await FakeS3Server.StartAsync(FakeS3Server.Error(HttpStatusCode.NotFound, "NoSuchKey"));
        using var store = Store(fake.Url);
        var key = ObjectKey.NewQuarantine();

        Assert.Null(await store.OpenReadAsync(key, Ct));
        await store.DeleteAsync(key, Ct);
    }

    [Fact]
    public async Task ARefusedConnectionIsAStorageUnavailableError()
    {
        using var store = Store(await FakeS3Server.ClosedUrlAsync());

        await Assert.ThrowsAsync<StorageUnavailableException>(() => store.OpenReadAsync(ObjectKey.NewQuarantine(), Ct));
    }

    [Fact]
    public async Task AStorageThatNeverAnswersIsAStorageUnavailableErrorAfterTheRequestTimeout()
    {
        await using var fake = await FakeS3Server.StartAsync(FakeS3Server.Hang());
        using var store = Store(fake.Url, requestTimeout: TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<StorageUnavailableException>(() => store.OpenReadAsync(ObjectKey.NewQuarantine(), Ct));
    }

    [Fact]
    public async Task ACancelledCallerIsNotMistakenForAStorageFailure()
    {
        await using var fake = await FakeS3Server.StartAsync(FakeS3Server.Hang());
        using var store = Store(fake.Url);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(200));

        var error = await Record.ExceptionAsync(() => store.OpenReadAsync(ObjectKey.NewQuarantine(), cancelled.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.False(error is IOException);
    }

    [Fact]
    public async Task ATransientFailureIsLoggedAsAWarningWithoutTheKey()
    {
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        await using var fake = await FakeS3Server.StartAsync(FakeS3Server.Error(HttpStatusCode.ServiceUnavailable, "SlowDown"));
        using var store = Store(fake.Url, loggerFactory.CreateLogger<S3ObjectStore>());
        var key = ObjectKey.NewQuarantine();

        await Assert.ThrowsAsync<StorageUnavailableException>(() => store.OpenReadAsync(key, Ct));

        var warning = Assert.Single(logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("GetObject", warning.Text, StringComparison.Ordinal);
        Assert.Contains("SlowDown", warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(key.Value, warning.Text, StringComparison.Ordinal);
    }

    private static S3ObjectStore Store(string url, ILogger<S3ObjectStore>? logger = null, TimeSpan? requestTimeout = null) =>
        new(
            new S3StorageOptions
            {
                ServiceUrl = url,
                Region = "us-east-1",
                Bucket = "duora-media-test",
                AccessKeyId = "synthetic-access",
                SecretAccessKey = "synthetic-secret",
                AllowInsecureHttp = true,
                RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30),
            },
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<S3ObjectStore>.Instance);
}
