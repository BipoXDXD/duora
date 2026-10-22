using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Duora.Infrastructure.Geolocation;
using Duora.IntegrationTests.Media;

namespace Duora.IntegrationTests.Geolocation;

public sealed class S3CountryDatabaseArchiveTests(MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OriginalBytesManifestAttributionAndPrivateAclSurviveRoundTripWithoutRenewal()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var m = Manifest(bytes);
        using var archive = new S3CountryDatabaseArchive(minio.CreateClient(), MinioFixture.Bucket, 1024);
        await using var input = new MemoryStream(bytes);
        await archive.PutAsync(m, input, Ct);
        using var client = minio.CreateClient();
        var first = await client.GetObjectMetadataAsync(MinioFixture.Bucket, Key(m, ".mmdb"), Ct);
        input.Position = 0;
        await archive.PutAsync(m, input, Ct);
        var repeated = await client.GetObjectMetadataAsync(MinioFixture.Bucket, Key(m, ".mmdb"), Ct);
        Assert.Equal(first.LastModified, repeated.LastModified);
        await using var restored = await archive.OpenAsync(m.Sha256, Ct);
        Assert.NotNull(restored);
        using var copy = new MemoryStream();
        await restored.CopyToAsync(copy, Ct);
        Assert.Equal(bytes, copy.ToArray());
        using var manifest = await client.GetObjectAsync(MinioFixture.Bucket, Key(m, ".manifest.json"), Ct);
        Assert.Equal(m, await JsonSerializer.DeserializeAsync<CountryDatabaseManifest>(manifest.ResponseStream, cancellationToken: Ct));
        using var notice = await client.GetObjectAsync(MinioFixture.Bucket, Key(m, ".NOTICE.txt"), Ct);
        using var reader = new StreamReader(notice.ResponseStream);
        var text = await reader.ReadToEndAsync(Ct);
        Assert.Contains("IPinfo", text, StringComparison.Ordinal);
        Assert.Contains("CC BY-SA 4.0", text, StringComparison.Ordinal);
        var acl = await client.GetObjectAclAsync(new GetObjectAclRequest { BucketName = MinioFixture.Bucket, Key = Key(m, ".mmdb") }, Ct);
        Assert.DoesNotContain(acl.Grants, g => g.Grantee.URI?.Contains("AllUsers", StringComparison.Ordinal) == true);
        await archive.DeleteAsync(m.Sha256, Ct);
        Assert.DoesNotContain(m.Sha256, await archive.ListAsync(Ct));
    }

    [Fact]
    public async Task DeletionRemovesAllOldVersionsDeleteMarkersOrphansAndMultipartUploads()
    {
        using var client = minio.CreateClient();
        await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = MinioFixture.Bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        }, Ct);
        var m = Manifest(Guid.NewGuid().ToByteArray());
        var key = Key(m, ".manifest.json");
        for (var i = 0; i < 3; i++)
        { await client.PutObjectAsync(new PutObjectRequest { BucketName = MinioFixture.Bucket, Key = key, ContentBody = "synthetic orphan" }, Ct); }
        await client.DeleteObjectAsync(MinioFixture.Bucket, key, Ct);
        await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = MinioFixture.Bucket, Key = Key(m, ".mmdb") }, Ct);
        using var archive = new S3CountryDatabaseArchive(minio.CreateClient(), MinioFixture.Bucket, 1024);
        Assert.Contains(m.Sha256, await archive.ListAsync(Ct));
        await archive.DeleteAsync(m.Sha256, Ct);
        Assert.DoesNotContain(m.Sha256, await archive.ListAsync(Ct));
        var versions = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = MinioFixture.Bucket, Prefix = "ipinfo-lite/" + m.Sha256 }, Ct);
        Assert.Empty(versions.Versions ?? []);
        var uploads = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = MinioFixture.Bucket, Prefix = "ipinfo-lite/" + m.Sha256 }, Ct);
        Assert.Empty(uploads.MultipartUploads ?? []);
    }

    [Fact]
    public async Task BadHashOversizedAndTamperedRemoteBytesAreRejected()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var m = Manifest(bytes);
        using var archive = new S3CountryDatabaseArchive(minio.CreateClient(), MinioFixture.Bucket, 8);
        await using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.PutAsync(m, input, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => archive.OpenAsync("../other", Ct));
        using var client = minio.CreateClient();
        await client.PutObjectAsync(new PutObjectRequest { BucketName = MinioFixture.Bucket, Key = Key(m, ".mmdb"), ContentBody = "bad" }, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.OpenAsync(m.Sha256, Ct));
        using var validBudget = new S3CountryDatabaseArchive(minio.CreateClient(), MinioFixture.Bucket, 1024);
        input.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => validBudget.PutAsync(m, input, Ct));
        await validBudget.DeleteAsync(m.Sha256, Ct);
    }

    private static string Key(CountryDatabaseManifest m, string suffix) => "ipinfo-lite/" + m.Sha256 + suffix;
    private static CountryDatabaseManifest Manifest(byte[] bytes)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, now.AddHours(-1), now, now,
            "synthetic-ipinfo-lite", "ipinfo_lite.mmdb", "ipinfo-lite-country-v1", "CC-BY-SA-4.0", "https://ipinfo.io/data/ipinfo_lite.mmdb");
    }
}
