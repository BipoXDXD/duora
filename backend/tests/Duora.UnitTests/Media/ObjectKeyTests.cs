using Duora.Core.Media;

namespace Duora.UnitTests.Media;

public sealed class ObjectKeyTests
{
    [Fact]
    public void AQuarantineKeyIsRandomAndLivesInTheQuarantineArea()
    {
        var first = ObjectKey.NewQuarantine();
        var second = ObjectKey.NewQuarantine();

        Assert.Equal(StorageArea.Quarantine, first.Area);
        Assert.StartsWith("quarantine/", first.Value, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.Matches("^quarantine/[0-9a-f]{32}$", first.Value);
    }

    [Theory]
    [InlineData(PhotoVariant.Thumb, "thumb")]
    [InlineData(PhotoVariant.Full, "full")]
    public void ADerivativeKeyIsStablePerUploadAndVariant(PhotoVariant variant, string name)
    {
        var uploadId = Guid.Parse("0199e8a0-1c2d-7ae3-87a5-305fddb27fd1");

        var key = ObjectKey.ForDerivative(uploadId, variant);

        Assert.Equal(StorageArea.Derivatives, key.Area);
        Assert.Equal($"derivatives/0199e8a01c2d7ae387a5305fddb27fd1-{name}", key.Value);
        Assert.Equal(key, ObjectKey.ForDerivative(uploadId, variant));
        Assert.Equal(key, ObjectKey.Parse(key.Value));
    }

    [Theory]
    [InlineData("quarantine/0123456789abcdef0123456789abcdef", StorageArea.Quarantine)]
    [InlineData("derivatives/0123456789abcdef0123456789abcdef-card", StorageArea.Derivatives)]
    public void AStoredKeyIsReadBackIntoItsArea(string stored, StorageArea area)
    {
        var key = ObjectKey.Parse(stored);

        Assert.Equal(area, key.Area);
        Assert.Equal(stored, key.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("quarantine/")]
    [InlineData("quarantine/../derivatives/x")]
    [InlineData("quarantine/ABC")]
    [InlineData("/quarantine/abc")]
    [InlineData("backup/abc")]
    [InlineData("quarantine/abc/def")]
    [InlineData("quarantine/foto.jpg")]
    [InlineData("quarantine\\abc")]
    [InlineData("quarantine/0123456789abcdef0123456789abcdef\n")]
    [InlineData("derivatives/0123456789abcdef0123456789abcdef-card\n")]
    public void AnythingElseIsNotAKey(string stored)
    {
        Assert.Throws<FormatException>(() => ObjectKey.Parse(stored));
    }

    [Fact]
    public void TheKeyNeverPrintsItselfInLogs()
    {
        // Chave interna do bucket não vai para log (TM-42): só a área aparece se alguém interpolar o valor.
        var key = ObjectKey.NewQuarantine();

        Assert.Equal("ObjectKey(quarantine)", key.ToString());
    }
}
