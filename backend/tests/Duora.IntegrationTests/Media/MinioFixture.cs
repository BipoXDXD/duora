using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Duora.Infrastructure.Media;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// MinIO descartável como dublê do S3 (ADR-0025). Imagem da Bitnami fixada por digest do índice multiarquitetura,
/// a mesma do drill de backup (#19): o projeto MinIO parou de publicar imagens oficiais. O servidor é AGPL-3.0 e
/// roda só como container de teste, sem ser ligado nem distribuído com o Duora. Credenciais sintéticas.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string Image =
        "bitnamilegacy/minio:2025.7.23-debian-12-r5@sha256:6dabb4a2088c9a79908de3bc05f4586c23ad2182c8908e7e3acbf61c1467fb20";

    public const string Bucket = "duora-media-test";

    private const int ApiPort = 9000;
    private const string AccessKey = "duora-test-access";

    private readonly string _secretKey = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    private readonly IContainer _container;

    public MinioFixture()
    {
        _container = new ContainerBuilder(Image)
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", _secretKey)
            .WithPortBinding(ApiPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(ApiPort)
                .ForPath("/minio/health/ready")))
            .Build();
    }

    internal S3StorageOptions Options => new()
    {
        ServiceUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(ApiPort)}",
        Region = "us-east-1",
        Bucket = Bucket,
        AccessKeyId = AccessKey,
        SecretAccessKey = _secretKey,
        AllowInsecureHttp = true,
    };

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        using var client = S3ObjectStore.CreateClient(Options);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Cliente com as mesmas credenciais, para o teste conferir o bucket por fora do adaptador.</summary>
    internal IAmazonS3 CreateClient() => S3ObjectStore.CreateClient(Options);
}
