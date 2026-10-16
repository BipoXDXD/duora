using Duora.Core.Media;
using Duora.Infrastructure.Media;

namespace Duora.IntegrationTests.Media;

public sealed class FileSystemObjectStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("duora-fs-store-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task WithoutCreatingDirectoriesAMissingAreaFailsAndNothingIsCreated()
    {
        // Em Production a área é uma montagem: se sumiu, gravar criaria a pasta no disco efêmero do container.
        var store = new FileSystemObjectStore(_root.FullName, createMissingDirectories: false);
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutAsync(
            ObjectKey.NewQuarantine(), content, content.Length, "image/png", TestContext.Current.CancellationToken));

        Assert.Empty(_root.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task ARootWithATrailingSeparatorStoresAndReadsBack()
    {
        var store = new FileSystemObjectStore(_root.FullName + Path.DirectorySeparatorChar);
        var key = ObjectKey.ForDerivative(ObjectKey.NewQuarantine(), PhotoVariant.Thumb);
        using var content = new MemoryStream([4, 5, 6]);

        await store.PutAsync(key, content, content.Length, "image/webp", TestContext.Current.CancellationToken);
        await using var read = await store.OpenReadAsync(key, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 4, 5, 6 }, copy.ToArray());
    }

    [Fact]
    public void TheMountedAreasAreTheAreasOfTheObjectKeys()
    {
        Assert.Equal(
            ["quarantine", "derivatives"],
            FileSystemObjectStore.AreaDirectories);
        Assert.StartsWith(FileSystemObjectStore.AreaDirectories[0] + "/", ObjectKey.NewQuarantine().Value, StringComparison.Ordinal);
        Assert.StartsWith(
            FileSystemObjectStore.AreaDirectories[1] + "/",
            ObjectKey.ForDerivative(ObjectKey.NewQuarantine(), PhotoVariant.Full).Value,
            StringComparison.Ordinal);
    }
}
