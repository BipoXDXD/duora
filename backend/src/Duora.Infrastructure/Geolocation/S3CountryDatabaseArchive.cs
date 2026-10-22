using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Duora.Infrastructure.Media;

namespace Duora.Infrastructure.Geolocation;

/// <summary>Dedicated private bucket, without Object Lock or replication. Keys contain only artifact hashes.</summary>
public sealed class S3CountryDatabaseArchive : ICountryDatabaseArchive, IDisposable
{
    private const string Prefix = "ipinfo-lite/";
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly long _maxBytes;

    public S3CountryDatabaseArchive(CountryDatabaseArchiveOptions options, long maxBytes)
        : this(S3ObjectStore.CreateClient(new S3StorageOptions
        {
            ServiceUrl = options.ServiceUrl,
            Region = options.Region,
            Bucket = options.Bucket,
            AccessKeyId = options.AccessKeyId,
            SecretAccessKey = options.SecretAccessKey,
            RequestTimeout = TimeSpan.FromSeconds(60),
        }), options.Bucket, maxBytes)
    { }

    internal S3CountryDatabaseArchive(IAmazonS3 client, string bucket, long maxBytes)
    { _client = client; _bucket = bucket; _maxBytes = maxBytes; }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in await Versions(Prefix, ct))
        { hashes.Add(HashFromKey(version.Key)); }
        foreach (var upload in await Uploads(Prefix, ct))
        { hashes.Add(HashFromKey(upload.Key)); }
        return hashes.ToArray();
    }

    public async Task PutAsync(CountryDatabaseManifest manifest, Stream content, CancellationToken ct)
    {
        if (!CountryDatabaseRuntimeOptions.ValidHash(manifest.Sha256) || !content.CanSeek
            || content.Position != 0 || content.Length != manifest.SizeBytes || content.Length > _maxBytes
            || Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct)) != manifest.Sha256)
        { throw new InvalidDataException("Country database archive content rejected."); }
        content.Position = 0;
        await Put(manifest.Sha256 + ".mmdb", content, manifest.Sha256, "application/octet-stream", ct);
        await using var metadata = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest));
        await Put(manifest.Sha256 + ".manifest.json", metadata,
            Convert.ToHexStringLower(SHA256.HashData(metadata.ToArray())), "application/json", ct);
        await using var notice = new MemoryStream(Encoding.UTF8.GetBytes(
            "IPinfo Lite — IPinfo.io\nSource: https://ipinfo.io/data/ipinfo_lite.mmdb\nLicense: CC BY-SA 4.0\nhttps://creativecommons.org/licenses/by-sa/4.0/\nOriginal provider bytes and original manifest; country projection only in Duora.\n"));
        await Put(manifest.Sha256 + ".NOTICE.txt", notice,
            Convert.ToHexStringLower(SHA256.HashData(notice.ToArray())), "text/plain; charset=utf-8", ct);
    }

    private async Task Put(string key, Stream content, string sha256, string type, CancellationToken ct)
    {
        try
        {
            var head = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = Prefix + key }, ct);
            if (head.ContentLength != content.Length || head.Metadata["sha256"] != sha256)
            { throw new InvalidDataException("Country database archive metadata mismatch."); }
            return; // No new B2 version or renewed upload date for an existing immutable artifact.
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { }
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = Prefix + key,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = type,
            UseChunkEncoding = false,
        };
        request.Metadata.Add("sha256", sha256);
        await _client.PutObjectAsync(request, ct);
    }

    public async Task<Stream?> OpenAsync(string hash, CancellationToken ct)
    {
        RequireHash(hash);
        try
        {
            using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = Prefix + hash + ".mmdb" }, ct);
            if (response.ContentLength <= 0 || response.ContentLength > _maxBytes)
            { throw new InvalidDataException("Country database archive size rejected."); }
            var memory = new MemoryStream();
            try
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await response.ResponseStream.ReadAsync(buffer, ct)) > 0)
                {
                    if (memory.Length + read > _maxBytes)
                    { throw new InvalidDataException("Country database archive size rejected."); }
                    await memory.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                memory.Position = 0;
                if (memory.Length != response.ContentLength || Convert.ToHexStringLower(await SHA256.HashDataAsync(memory, ct)) != hash)
                { throw new InvalidDataException("Country database archive content rejected."); }
                memory.Position = 0;
                return memory;
            }
            catch { await memory.DisposeAsync(); throw; }
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task DeleteAsync(string hash, CancellationToken ct)
    {
        RequireHash(hash);
        var prefix = Prefix + hash + ".";
        foreach (var version in await Versions(prefix, ct))
        {
            if (HashFromKey(version.Key) != hash)
            { throw new InvalidDataException("Country database archive key rejected."); }
            await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = version.Key, VersionId = version.VersionId }, ct);
        }
        foreach (var upload in await Uploads(prefix, ct))
        { await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = upload.Key, UploadId = upload.UploadId }, ct); }
        // An immutable key can have delete markers or old B2 versions: confirm all are gone.
        if ((await Versions(prefix, ct)).Count != 0 || (await Uploads(prefix, ct)).Count != 0)
        { throw new IOException("Country database archive deletion incomplete."); }
    }

    private async Task<List<S3ObjectVersion>> Versions(string prefix, CancellationToken ct)
    {
        var result = new List<S3ObjectVersion>();
        var request = new ListVersionsRequest { BucketName = _bucket, Prefix = prefix, MaxKeys = 1000 };
        do
        {
            var response = await _client.ListVersionsAsync(request, ct);
            result.AddRange(response.Versions ?? []);
            if (result.Count > 4096)
            { throw new InvalidDataException("Country database archive inventory exceeds budget."); }
            if (response.IsTruncated != true)
            { return result; }
            if (string.IsNullOrEmpty(response.NextKeyMarker) || (request.KeyMarker == response.NextKeyMarker && request.VersionIdMarker == response.NextVersionIdMarker))
            { throw new InvalidDataException("Country database archive pagination rejected."); }
            request.KeyMarker = response.NextKeyMarker;
            request.VersionIdMarker = response.NextVersionIdMarker;
        } while (true);
    }

    private async Task<List<MultipartUpload>> Uploads(string prefix, CancellationToken ct)
    {
        var result = new List<MultipartUpload>();
        var request = new ListMultipartUploadsRequest { BucketName = _bucket, Prefix = prefix, MaxUploads = 1000 };
        do
        {
            var response = await _client.ListMultipartUploadsAsync(request, ct);
            result.AddRange(response.MultipartUploads ?? []);
            if (result.Count > 4096)
            { throw new InvalidDataException("Country database archive inventory exceeds budget."); }
            if (response.IsTruncated != true)
            { return result; }
            if (string.IsNullOrEmpty(response.NextKeyMarker) || (request.KeyMarker == response.NextKeyMarker && request.UploadIdMarker == response.NextUploadIdMarker))
            { throw new InvalidDataException("Country database archive pagination rejected."); }
            request.KeyMarker = response.NextKeyMarker;
            request.UploadIdMarker = response.NextUploadIdMarker;
        } while (true);
    }

    private static string HashFromKey(string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length < Prefix.Length + 65)
        { throw new InvalidDataException("Country database archive key rejected."); }
        var hash = key.Substring(Prefix.Length, 64);
        RequireHash(hash);
        if (key[(Prefix.Length + 64)..] is not (".mmdb" or ".manifest.json" or ".NOTICE.txt"))
        { throw new InvalidDataException("Country database archive key rejected."); }
        return hash;
    }

    private static void RequireHash(string hash)
    {
        if (!CountryDatabaseRuntimeOptions.ValidHash(hash))
        { throw new ArgumentException("Country database hash rejected.", nameof(hash)); }
    }

    public void Dispose() => _client.Dispose();
}
