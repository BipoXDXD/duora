using Duora.Web.Identity;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Leitura de /proc/self/mountinfo (formato em proc(5)): o key ring só conta como persistente se o diretório for o
/// ponto de montagem de um volume, nunca a camada gravável do container nem memória.
/// </summary>
public sealed class KeyRingMountTests
{
    private const string KeysDirectory = "/home/app/.aspnet/DataProtection-Keys";

    private const string Overlay =
        "640 560 0:52 / / ro,relatime master:301 - overlay overlay rw,lowerdir=/var/lib/docker/l,upperdir=/u,workdir=/w";

    private const string Proc = "641 640 0:55 / /proc rw,nosuid,nodev,noexec,relatime - proc proc rw";

    private const string NamedVolume =
        "655 640 8:1 /var/lib/docker/volumes/duora_dataprotection/_data /home/app/.aspnet/DataProtection-Keys " +
        "rw,relatime - ext4 /dev/sda1 rw,discard";

    private const string Tmpfs = "656 640 0:60 / /home/app/.aspnet/DataProtection-Keys rw,nosuid - tmpfs tmpfs rw,size=1024k";

    [Fact]
    public void NamedVolumeAtTheKeysDirectoryIsPersistent()
    {
        Assert.True(KeyRingMount.IsPersistentMountPoint(KeysDirectory, [Overlay, Proc, NamedVolume]));
    }

    [Fact]
    public void TrailingSlashInTheConfiguredDirectoryStillMatchesTheMountPoint()
    {
        Assert.True(KeyRingMount.IsPersistentMountPoint(KeysDirectory + "/", [Overlay, NamedVolume]));
    }

    [Fact]
    public void DirectoryInsideTheContainerLayerIsNotPersistent()
    {
        Assert.False(KeyRingMount.IsPersistentMountPoint(KeysDirectory, [Overlay, Proc]));
    }

    [Fact]
    public void EmptyMountTableIsNotPersistent()
    {
        Assert.False(KeyRingMount.IsPersistentMountPoint(KeysDirectory, []));
    }

    [Fact]
    public void SubdirectoryOfAVolumeIsNotTheMountPoint()
    {
        Assert.False(KeyRingMount.IsPersistentMountPoint(KeysDirectory + "/sub", [Overlay, NamedVolume]));
    }

    [Fact]
    public void ParentOfTheMountPointIsNotPersistent()
    {
        Assert.False(KeyRingMount.IsPersistentMountPoint("/home/app/.aspnet", [Overlay, NamedVolume]));
    }

    [Theory]
    [InlineData("tmpfs")]
    [InlineData("ramfs")]
    [InlineData("overlay")]
    public void MemoryOrContainerLayerFilesystemIsNotPersistent(string filesystem)
    {
        var line = $"656 640 0:60 / {KeysDirectory} rw - {filesystem} none rw";

        Assert.False(KeyRingMount.IsPersistentMountPoint(KeysDirectory, [Overlay, line]));
    }

    [Fact]
    public void LastMountOnTheSamePointWins()
    {
        Assert.False(KeyRingMount.IsPersistentMountPoint(KeysDirectory, [Overlay, NamedVolume, Tmpfs]));
        Assert.True(KeyRingMount.IsPersistentMountPoint(KeysDirectory, [Overlay, Tmpfs, NamedVolume]));
    }

    [Fact]
    public void OctalEscapedSpaceInTheMountPointIsDecoded()
    {
        var line = @"657 640 8:1 /data /srv/duora\040keys rw - ext4 /dev/sda1 rw";

        Assert.True(KeyRingMount.IsPersistentMountPoint("/srv/duora keys", [Overlay, line]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("lixo")]
    [InlineData("1 2 3 4 /home/app/.aspnet/DataProtection-Keys rw sem-separador ext4")]
    public void MalformedLineIsIgnored(string line)
    {
        Assert.False(KeyRingMount.IsPersistentMountPoint(KeysDirectory, [Overlay, line]));
    }
}
