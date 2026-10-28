using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Duora.Core.Media;

/// <summary>
/// Pure V1 binding for a future durable CreateOrGet. Neither the hash nor this value proves Prepared,
/// snapshot provenance, SQL state, provider atomicity or a current restore fence.
/// </summary>
public sealed class PhotoDeletionIntentBinding
{
    private PhotoDeletionIntentBinding(Guid environmentId, Guid photoId, long expectedPhotoVersion,
        string snapshotSha256, string idempotencyKeyId, string operationKey, string intentSha256)
    {
        EnvironmentId = environmentId;
        PhotoId = photoId;
        ExpectedPhotoVersion = expectedPhotoVersion;
        SnapshotSha256 = snapshotSha256;
        IdempotencyKeyId = idempotencyKeyId;
        OperationKey = operationKey;
        IntentSha256 = intentSha256;
    }

    public Guid EnvironmentId { get; }
    public Guid PhotoId { get; }
    public long ExpectedPhotoVersion { get; }
    public string SnapshotSha256 { get; }
    public string IdempotencyKeyId { get; }
    public string OperationKey { get; }
    public string IntentSha256 { get; }

    /// <summary>
    /// Material and key ID must remain stable across retries/restores. Reusing the returned operation key
    /// with another intent must conflict at the real port; this helper never overwrites a stored binding.
    /// </summary>
    public static PhotoDeletionIntentBinding Create(Guid environmentId, Guid ownerId, IdempotencyKey clientKey,
        ReadOnlySpan<byte> operationKeyMaterial, string keyId, Guid photoId, long expectedPhotoVersion,
        ReadOnlySpan<byte> snapshotSha256)
    {
        if (environmentId == Guid.Empty || ownerId == Guid.Empty || photoId == Guid.Empty)
        {
            throw new ArgumentException("Deletion binding requires nonempty identities.");
        }
        if (!IdempotencyKey.TryParse(clientKey.Value, out _) || !IdempotencyKey.TryParse(keyId, out _))
        {
            throw new ArgumentException("Deletion binding requires safe idempotency keys and key identifiers.");
        }
        if (operationKeyMaterial.Length is < 32 or > 64)
        {
            throw new ArgumentException("Deletion binding requires 32 to 64 bytes of dedicated stable material.", nameof(operationKeyMaterial));
        }
        if (snapshotSha256.Length != 32)
        {
            throw new ArgumentException("Deletion binding requires one captured SHA256 digest.", nameof(snapshotSha256));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedPhotoVersion, 1);

        var material = operationKeyMaterial.ToArray();
        var operationPayload = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"DUORA-PHOTO-DELETE-OP-V1\n{keyId}\n{environmentId:D}\n{ownerId:D}\n{clientKey.Value}\n"));
        string operationKey;
        try
        {
            operationKey = Convert.ToHexStringLower(HMACSHA256.HashData(material, operationPayload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
            CryptographicOperations.ZeroMemory(operationPayload);
        }
        var snapshot = Convert.ToHexStringLower(snapshotSha256);
        var intentPayload = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"DUORA-PHOTO-DELETE-INTENT-V1\n{environmentId:D}\n{operationKey}\n{photoId:D}\n{expectedPhotoVersion}\n{snapshot}\n"));
        var intent = Convert.ToHexStringLower(SHA256.HashData(intentPayload));
        return new PhotoDeletionIntentBinding(environmentId, photoId, expectedPhotoVersion, snapshot, keyId,
            operationKey, intent);
    }

    public override string ToString() => nameof(PhotoDeletionIntentBinding);
}
