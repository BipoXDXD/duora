using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Media;

public sealed class PrivateGenerationRoutingTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("duora-private-routing-");

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAuthorityClientNeverPublishesPrivateBytesThroughTheGenericPut(bool promotion)
    {
        using var provider = Services("Testing").BuildServiceProvider();
        var store = provider.GetRequiredService<IObjectStore>();
        var generation = Guid.NewGuid();
        var key = promotion ? ObjectKey.ForPromotedDerivative(generation, PhotoVariant.Full)
            : ObjectKey.ForReviewCandidate(generation, PhotoVariant.Full);
        using var bytes = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutAsync(
            key, bytes, 3, "image/webp", TestContext.Current.CancellationToken));

        Assert.Empty(_root.EnumerateFiles("*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    [InlineData("Unknown")]
    public void UnqualifiedAuthorityCannotBeEnabledOutsideTesting(string environment)
    {
        foreach (var area in FileSystemObjectStore.AreaDirectories)
        {
            var directory = _root.CreateSubdirectory(area);
            File.WriteAllText(Path.Combine(directory.FullName, FileSystemObjectStore.AreaMarkerFileName), "synthetic");
        }
        using var provider = Services(environment).BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IObjectStore>());
        Assert.Contains("unqualified", error.Message, StringComparison.Ordinal);
    }

    private IServiceCollection Services(string environment)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Media:Storage:Provider"] = "FileSystem",
            ["Media:Storage:FileSystemRoot"] = _root.FullName,
            ["Media:PrivateGenerations:Enabled"] = "true",
            ["Media:PrivateGenerations:SocketPath"] = Path.Combine(_root.FullName, "authority.sock"),
            ["Media:PrivateGenerations:AuthorityUid"] = "17010",
            ["Media:PrivateGenerations:DomainId"] = "eb0f3209-07c4-42a8-870c-4d223a756569",
        };
        return new ServiceCollection().AddLogging().AddMediaUploads(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), environment);
    }
}
