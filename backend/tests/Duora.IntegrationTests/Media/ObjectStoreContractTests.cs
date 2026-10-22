using Amazon.S3;
using Amazon.S3.Model;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Duora.IntegrationTests.Media;

public sealed class FileSystemObjectStoreContractTests : ObjectStoreContract, IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("duora-media-");

    public FileSystemObjectStoreContractTests()
    {
        Store = new FileSystemObjectStore(_root.FullName);
    }

    protected override IObjectStore Store { get; }

    [Fact]
    public async Task AFailedWriteLeavesNoTemporaryFileBehind()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var shortStream = new ForwardOnlyStream(new byte[10]);
            await Store.PutAsync(ObjectKey.NewQuarantine(), shortStream, 20, "image/png", TestContext.Current.CancellationToken);
        });

        Assert.Empty(_root.EnumerateFiles("*", SearchOption.AllDirectories));
    }

    public void Dispose() => _root.Delete(recursive: true);
}

public sealed class S3ObjectStoreContractTests(MinioFixture minio) : ObjectStoreContract, IClassFixture<MinioFixture>
{
    protected override IObjectStore Store { get; } = new S3ObjectStore(minio.Options, NullLogger<S3ObjectStore>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObjectsLandInTheConfiguredBucketUnderTheKeyAndNeverPublic(bool reviewCandidate)
    {
        var key = reviewCandidate
            ? ObjectKey.ForReviewCandidate(Guid.NewGuid(), PhotoVariant.Card)
            : ObjectKey.NewQuarantine();
        await PutAsync(key, [1, 2, 3]);

        using var client = minio.CreateClient();
        var acl = await client.GetObjectAclAsync(
            new GetObjectAclRequest { BucketName = MinioFixture.Bucket, Key = key.Value },
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(acl.Grants, grant => grant.Grantee.URI?.Contains("AllUsers", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(acl.Grants, grant => grant.Grantee.URI?.Contains("AuthenticatedUsers", StringComparison.Ordinal) == true);
        using var anonymous = new HttpClient();
        using var response = await anonymous.GetAsync(
            $"{minio.Options.ServiceUrl}/{MinioFixture.Bucket}/{key.Value}", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ABucketThatDoesNotExistIsAnErrorNotAMissingObject()
    {
        // Configuração errada não pode parecer "foto apagada": o erro sobe para o retry e o alerta.
        var misconfigured = new S3ObjectStore(minio.Options with { Bucket = "bucket-que-nao-existe" }, NullLogger<S3ObjectStore>.Instance);

        await Assert.ThrowsAsync<AmazonS3Exception>(
            () => misconfigured.OpenReadAsync(ObjectKey.NewQuarantine(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWrongCredentialIsAnErrorLoggedWithoutTheKey()
    {
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var wrongSecret = new S3ObjectStore(minio.Options with { SecretAccessKey = "segredo-errado" }, loggerFactory.CreateLogger<S3ObjectStore>());
        var key = ObjectKey.NewQuarantine();

        await Assert.ThrowsAsync<AmazonS3Exception>(() => wrongSecret.DeleteAsync(key, TestContext.Current.CancellationToken));

        var error = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("DeleteObject", error.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(key.Value, error.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("segredo-errado", error.Text, StringComparison.Ordinal);
    }
}
