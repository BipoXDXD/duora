using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class PhotoDeletionIntentBindingTests
{
    private static readonly Guid EnvironmentId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid OwnerId = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid PhotoId = Guid.Parse("10000000-0000-4000-8000-000000000003");
    private static readonly Guid OtherId = Guid.Parse("10000000-0000-4000-8000-000000000004");

    [Fact]
    public void IndependentSyntheticKnownAnswersPinTheVersionedCanonicalBytes()
    {
        var binding = Create();

        Assert.Equal("f9a8a1ebbb8b4e131b9815e1b6abfdbf7dd9d0fc5db48de7645d45f921564dcc", binding.OperationKey);
        Assert.Equal("b2b8977fca3c691b847979a2d33396c97917773cb4e9594729c3228189cfef24", binding.IntentSha256);
        Assert.Equal(EnvironmentId, binding.EnvironmentId);
        Assert.Equal(PhotoId, binding.PhotoId);
        Assert.Equal(7, binding.ExpectedPhotoVersion);
        Assert.Equal("synthetic-v1", binding.IdempotencyKeyId);
        Assert.Equal("202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f", binding.SnapshotSha256);
    }

    [Fact]
    public void RepeatingTheSameCapturedIntentProducesTheSameBinding()
    {
        var first = Create();
        var replay = Create();

        Assert.Equal(first.OperationKey, replay.OperationKey);
        Assert.Equal(first.IntentSha256, replay.IntentSha256);
    }

    [Theory]
    [InlineData("photo")]
    [InlineData("version")]
    [InlineData("snapshot")]
    public void ChangedIntentKeepsTheOperationKeySoTheRealPortMustConflict(string change)
    {
        var original = Create();
        var changed = Create(photoId: change == "photo" ? OtherId : null,
            version: change == "version" ? 8 : 7,
            snapshot: change == "snapshot" ? new byte[32] : null);

        Assert.Equal(original.OperationKey, changed.OperationKey);
        Assert.NotEqual(original.IntentSha256, changed.IntentSha256);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("owner")]
    [InlineData("client-key")]
    [InlineData("key-id")]
    [InlineData("key-material")]
    public void EveryOperationScopeInputSeparatesTheKeyAndIntent(string change)
    {
        var original = Create();
        var changed = Create(environmentId: change == "environment" ? OtherId : null,
            ownerId: change == "owner" ? OtherId : null,
            clientKey: change == "client-key" ? "different-key" : "delete_2026_001",
            keyId: change == "key-id" ? "synthetic-v2" : "synthetic-v1",
            material: change == "key-material" ? new byte[32] : null);

        Assert.NotEqual(original.OperationKey, changed.OperationKey);
        Assert.NotEqual(original.IntentSha256, changed.IntentSha256);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("owner")]
    [InlineData("photo")]
    public void EmptyIdentityCannotBeBound(string identity)
    {
        Assert.Throws<ArgumentException>(() => Create(
            environmentId: identity == "environment" ? Guid.Empty : null,
            ownerId: identity == "owner" ? Guid.Empty : null,
            photoId: identity == "photo" ? Guid.Empty : null));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AnExpectedVersionMustBePositive(long version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(version: version));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(65)]
    public void OperationMaterialOutsideTheAdmittedLengthIsRefused(int length)
    {
        Assert.Throws<ArgumentException>(() => Create(material: new byte[length]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void ASnapshotMustHaveExactlyOneSha256Digest(int length)
    {
        Assert.Throws<ArgumentException>(() => Create(snapshot: new byte[length]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsafe\nfield")]
    [InlineData("chaveç")]
    public void AnUnsafeKeyIdentifierCannotChangeTheCanonicalFraming(string? keyId)
    {
        Assert.Throws<ArgumentException>(() => Create(keyId: keyId));
    }

    [Fact]
    public void AnOversizedKeyIdentifierIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Create(keyId: new string('k', 65)));
    }

    [Fact]
    public void ADefaultClientKeyCannotBeBound()
    {
        Assert.Throws<ArgumentException>(() => PhotoDeletionIntentBinding.Create(EnvironmentId, OwnerId,
            default, Material(), "synthetic-v1", PhotoId, 7, Snapshot()));
    }

    [Fact]
    public void InputArraysRemainOwnedByTheCallerAndCannotChangeTheResult()
    {
        var material = Material();
        var snapshot = Snapshot();
        var binding = Create(material: material, snapshot: snapshot);
        var expected = Create();
        Assert.Equal(Material(), material);
        Assert.Equal(Snapshot(), snapshot);

        Array.Fill(material, (byte)0xff);
        Array.Fill(snapshot, (byte)0xff);

        Assert.Equal(expected.OperationKey, binding.OperationKey);
        Assert.Equal(expected.IntentSha256, binding.IntentSha256);
        Assert.Equal(expected.SnapshotSha256, binding.SnapshotSha256);
        Assert.Equal(nameof(PhotoDeletionIntentBinding), binding.ToString());
    }

    [Fact]
    public void TheUpperMaterialAndIdentifierBoundariesAreAdmittedWithTheLargestVersion()
    {
        var binding = Create(material: new byte[64], keyId: new string('k', 64), version: long.MaxValue);

        Assert.Equal(64, binding.OperationKey.Length);
        Assert.Equal(64, binding.IntentSha256.Length);
        Assert.Equal(long.MaxValue, binding.ExpectedPhotoVersion);
    }

    private static PhotoDeletionIntentBinding Create(Guid? environmentId = null, Guid? ownerId = null,
        Guid? photoId = null, long version = 7, byte[]? material = null, byte[]? snapshot = null,
        string clientKey = "delete_2026_001", string? keyId = "synthetic-v1")
    {
        Assert.True(IdempotencyKey.TryParse(clientKey, out var key));
        return PhotoDeletionIntentBinding.Create(environmentId ?? EnvironmentId, ownerId ?? OwnerId,
            key, material ?? Material(), keyId!, photoId ?? PhotoId, version, snapshot ?? Snapshot());
    }

    private static byte[] Material() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static byte[] Snapshot() => Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
}
