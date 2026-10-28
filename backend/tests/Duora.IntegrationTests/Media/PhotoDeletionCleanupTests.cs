using System.Net;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Exclusão física de API-M03: só obrigações gravadas pela exclusão lógica confirmada, uma chave exata por vez,
/// com lease, retry durável e I/O de storage fora de transação. Banco próprio, porque o coletor varre todas as contas.
/// </summary>
[Trait("Acceptance", "T-26")]
public sealed class PhotoDeletionCleanupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private string _connectionString = "";
    private DuoraWebFactory? _factory;
    private MediaScenario? _media;
    private NpgsqlDataSource? _source;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MediaScenario Media => _media ?? throw new InvalidOperationException("Teste não inicializado.");

    private NpgsqlDataSource Source => _source ?? throw new InvalidOperationException("Teste não inicializado.");

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
        _factory = new DuoraWebFactory(_connectionString) { Time = _time };
        _media = new MediaScenario(_connectionString, _factory);
        _source = NpgsqlDataSource.Create(_connectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheCollectorRemovesEachDerivativeOfTheDeletedPhotoOnly()
    {
        var (deleted, asset, kept, keptAsset) = await TwoApprovedPhotosOneDeletedAsync();
        var dispatcher = Dispatcher(Media.Store);

        var processed = 0;
        while (await dispatcher.DispatchOnceAsync(Ct) is var count && count != 0)
        {
            processed += count;
        }

        Assert.Equal(4, processed);
        foreach (var (key, _) in asset.Variants.Values)
        {
            Assert.False(await Media.ObjectExistsAsync(key));
        }

        foreach (var (key, _) in keptAsset.Variants.Values)
        {
            Assert.True(await Media.ObjectExistsAsync(key));
        }

        Assert.Null(await Media.ReadQuarantineAsync(deleted));
        Assert.NotNull(await Media.ReadQuarantineAsync(kept));
        Assert.Equal(0, await Media.CountAsync("select count(*) from media.photo_deletion_cleanup"));
        Assert.Equal(1, await Media.CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", deleted.Id));
        Assert.Equal(3, await Media.CountAsync("select count(*) from media.media_variants where media_asset_id = $1", asset.AssetId));
    }

    [Fact]
    public async Task AStorageFailureKeepsTheObligationWithBackoffAndTheBytes()
    {
        var (deleted, asset, _, _) = await TwoApprovedPhotosOneDeletedAsync();
        var dispatcher = Dispatcher(new RefusingDeleteStore(Media.Store));

        Assert.Equal(1, await dispatcher.DispatchOnceAsync(Ct));

        Assert.Equal(4, await Media.CountAsync("select count(*) from media.photo_deletion_cleanup where photo_id = $1", deleted.Id));
        Assert.Equal(1, await Media.CountAsync(
            "select count(*) from media.photo_deletion_cleanup where attempts = 1 and lease_until is null and fencing_token is null and available_at > $1",
            _time.GetUtcNow()));
        foreach (var (key, _) in asset.Variants.Values)
        {
            Assert.True(await Media.ObjectExistsAsync(key));
        }

        _time.Advance(TimeSpan.FromMinutes(20));
        var recovered = Dispatcher(Media.Store);
        while (await recovered.DispatchOnceAsync(Ct) != 0)
        {
        }

        Assert.Equal(0, await Media.CountAsync("select count(*) from media.photo_deletion_cleanup"));
        foreach (var (key, _) in asset.Variants.Values)
        {
            Assert.False(await Media.ObjectExistsAsync(key));
        }
    }

    [Fact]
    public async Task ALeaseHeldByAnotherCollectorIsNotTakenUntilItExpires()
    {
        await TwoApprovedPhotosOneDeletedAsync();
        await Media.ExecuteAsync(
            "update media.photo_deletion_cleanup set fencing_token = $1, lease_until = $2",
            Guid.NewGuid(),
            _time.GetUtcNow().AddSeconds(30));
        var dispatcher = Dispatcher(Media.Store);

        Assert.Equal(0, await dispatcher.DispatchOnceAsync(Ct));

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await dispatcher.DispatchOnceAsync(Ct));
        Assert.Equal(3, await Media.CountAsync("select count(*) from media.photo_deletion_cleanup"));
    }

    [Fact]
    public async Task AnObligationWhosePhotoIsNoLongerDeletedIsNeverClaimed()
    {
        var (deleted, asset, _, _) = await TwoApprovedPhotosOneDeletedAsync();
        await Media.ExecuteAsync("update media.uploads set state = 'Approved' where id = $1", deleted.Id);
        var dispatcher = Dispatcher(Media.Store);

        Assert.Equal(0, await dispatcher.DispatchOnceAsync(Ct));

        Assert.Equal(4, await Media.CountAsync("select count(*) from media.photo_deletion_cleanup where photo_id = $1", deleted.Id));
        foreach (var (key, _) in asset.Variants.Values)
        {
            Assert.True(await Media.ObjectExistsAsync(key));
        }
    }

    [Fact]
    public async Task ADisabledCollectorDoesNothing()
    {
        var (_, asset, _, _) = await TwoApprovedPhotosOneDeletedAsync();
        var dispatcher = new PhotoDeletionCleanupDispatcher(Source, Media.Store, _time, enabled: false,
            NullLogger<PhotoDeletionCleanupDispatcher>.Instance);

        Assert.Equal(0, await dispatcher.DispatchOnceAsync(Ct));

        Assert.Equal(4, await Media.CountAsync("select count(*) from media.photo_deletion_cleanup"));
        Assert.True(await Media.ObjectExistsAsync(asset.Variants[PhotoVariant.Full].Key));
    }

    private PhotoDeletionCleanupDispatcher Dispatcher(IObjectStore store) =>
        new(Source, store, _time, enabled: true, NullLogger<PhotoDeletionCleanupDispatcher>.Instance);

    private async Task<(StoredUpload Deleted, SeededAsset Asset, StoredUpload Kept, SeededAsset KeptAsset)> TwoApprovedPhotosOneDeletedAsync()
    {
        var account = await Media.Identity.CreateConfirmedAccountAsync();
        using var browser = await Media.Identity.SignedInBrowserAsync(account);
        var deleted = await UploadAsync(browser, account);
        var kept = await UploadAsync(browser, account);
        var asset = await Media.ApproveWithVariantsAsync(deleted);
        var keptAsset = await Media.ApproveWithVariantsAsync(kept);
        using var response = await PhotoDeletionTests.DeleteAsync(browser, deleted.Id, $"\"photo-{deleted.Id:N}-3\"", MediaScenario.NewKey());
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return (deleted, asset, kept, keptAsset);
    }

    private async Task<StoredUpload> UploadAsync(Browser browser, TestAccount account)
    {
        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var photoId = Guid.Parse((string)(await MediaScenario.ReadObjectAsync(response))["photoId"]!);
        return Assert.Single(await Media.UploadsOfAsync(account.UserId), upload => upload.Id == photoId);
    }

    private sealed class RefusingDeleteStore(IObjectStore inner) : IObjectStore
    {
        public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, contentType, cancellationToken);

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new IOException("Storage indisponível no teste.");
    }
}
