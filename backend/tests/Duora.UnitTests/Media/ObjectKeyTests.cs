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
    public void ADerivativeKeyReusesTheRandomTokenOfTheQuarantineKey(PhotoVariant variant, string name)
    {
        // A chave do derivado vem do token aleatório do upload, não do photoId público (TM-41), e é a mesma em toda
        // tentativa (ADR-0025 item 6).
        var quarantine = ObjectKey.Parse("quarantine/0123456789abcdef0123456789abcdef");

        var key = ObjectKey.ForDerivative(quarantine, variant);

        Assert.Equal(StorageArea.Derivatives, key.Area);
        Assert.Equal($"derivatives/0123456789abcdef0123456789abcdef-{name}", key.Value);
        Assert.Equal(key, ObjectKey.ForDerivative(ObjectKey.Parse(quarantine.Value), variant));
        Assert.Equal(key, ObjectKey.Parse(key.Value));
    }

    [Fact]
    public void ADerivativeKeyComesOnlyFromAQuarantineKey()
    {
        var derivative = ObjectKey.Parse("derivatives/0123456789abcdef0123456789abcdef-card");

        Assert.Throws<ArgumentException>(() => ObjectKey.ForDerivative(derivative, PhotoVariant.Thumb));
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
