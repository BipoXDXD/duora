using System.Collections.Concurrent;
using System.Net;
using Duora.Core.Media;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// API-M04 entre a primeira autorização e o primeiro byte, e com o storage falhando (docs/api/media-review.md,
/// ADR-0010): o objeto é aberto sem transação, então uma exclusão confirmada no meio do caminho não deixa o
/// conteúdo sair, e o stream aberto é descartado. Falha do storage nunca mostra exceção, chave nem SQL ao cliente.
/// </summary>
[Trait("Acceptance", "T-26")]
public sealed class OwnerMediaReadConsistencyTests : IAsyncDisposable
{
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DuoraWebFactory _factory;
    private readonly MediaScenario _media;
    private readonly ProbeStore _store;

    public OwnerMediaReadConsistencyTests(PostgresFixture postgres)
    {
        _store = new ProbeStore(() => MediaStore);
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs)
        {
            TestServices = services => services.AddSingleton<IObjectStore>(_store),
        };
        _media = new MediaScenario(postgres, _factory);
    }

    private IObjectStore MediaStore => _media.Store;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task AnUnrelatedChangeInTheMeantimeStillServesTheBytesAndReleasesTheStream()
    {
        var (_, browser, upload, asset) = await ApprovedAsync();
        using var lifetime = browser;
        _store.AfterOpen = async _ => await _media.ExecuteAsync(
            "update media.media_variants set width = width + 1 where media_asset_id = $1", asset.AssetId);

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}?variant=full");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(asset.Variants[PhotoVariant.Full].Bytes, await response.Content.ReadAsByteArrayAsync(Ct));
        var opened = Assert.Single(_store.Opened);
        Assert.True(opened.Disposed, "O stream do storage tem de ser descartado depois da resposta.");
        Assert.Equal(1, await _media.CountAsync(
            "select count(*) from media.uploads where id = $1 and state = 'Approved'", upload.Id));
    }

    [Theory]
    [InlineData("asset-deleted")]
    [InlineData("asset-rejected")]
    [InlineData("asset-deleted-at")]
    [InlineData("upload-deleted")]
    [InlineData("variant-removed")]
    [InlineData("variant-moved")]
    [InlineData("asset-owner-changed")]
    [InlineData("upload-owner-changed")]
    [InlineData("photo-owner-changed")]
    public async Task AChangeAfterTheObjectWasOpenedIs404WithoutBytesAndTheStreamIsReleased(string change)
    {
        var (_, browser, upload, asset) = await ApprovedAsync();
        using var lifetime = browser;
        var newOwner = change.EndsWith("owner-changed", StringComparison.Ordinal)
            ? (await _media.Identity.CreateConfirmedAccountAsync()).UserId
            : Guid.Empty;
        _store.AfterOpen = _ => ApplyAsync(change, upload, asset, newOwner);
        using var missing = await browser.GetAsync($"/api/v1/media/{Guid.NewGuid()}");

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}?variant=full");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(
            await ProblemAssert.BodyWithoutTraceIdAsync(missing),
            await ProblemAssert.BodyWithoutTraceIdAsync(response));
        Assert.NotEqual("image/webp", response.Content.Headers.ContentType?.MediaType);
        var opened = Assert.Single(_store.Opened);
        Assert.True(opened.Disposed, "Sem autorização no segundo passo, o stream aberto não pode ficar para trás.");
        Assert.DoesNotContain(
            _logs.Entries,
            entry => entry.Text.Contains(asset.Variants[PhotoVariant.Full].Key.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIOFailureWhenOpeningTheDerivativeIs503WithRetryAfterAndWithoutInternals()
    {
        var (_, browser, upload, asset) = await ApprovedAsync();
        using var lifetime = browser;
        var key = asset.Variants[PhotoVariant.Full].Key.Value;
        var canary = $"CANARY-{Guid.NewGuid():N}";
        _store.OpenFailure = new IOException($"{canary} falha em {key}: select * from media.media_variants");

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}?variant=full");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(2, ProblemAssert.RetryAfterSeconds(response));
        Assert.NotEqual("image/webp", response.Content.Headers.ContentType?.MediaType);
        await AssertNoInternalsAsync(response, canary, key);
        Assert.Empty(_store.Opened);
        await AssertUploadUntouchedAsync(upload);
    }

    // Só IOException (disco, volume) é tratada como indisponibilidade passageira pelo gate das rotas M03/M04
    // (MediaAdmission). Falta de permissão no filesystem e as falhas de rede do SDK de um storage S3 não são
    // IOException e viram 500 genérico, apesar de docs/api/media-review.md pedir 503 com Retry-After para storage
    // transitório: a caracterização registra o que existe hoje, sem decidir por quem abre a issue.
    [Theory]
    [InlineData("permission-denied")]
    [InlineData("network-failure")]
    public async Task AStorageFailureThatIsNotAnIOExceptionIsAGeneric500WithoutInternals(string kind)
    {
        var (_, browser, upload, asset) = await ApprovedAsync();
        using var lifetime = browser;
        var key = asset.Variants[PhotoVariant.Full].Key.Value;
        var canary = $"CANARY-{Guid.NewGuid():N}";
        var message = $"{canary} falha em {key}: select * from media.media_variants";
        _store.OpenFailure = kind == "permission-denied"
            ? new UnauthorizedAccessException(message)
            : new HttpRequestException(message);

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}?variant=full");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        Assert.False(response.Headers.Contains("Retry-After"));
        Assert.NotEqual("image/webp", response.Content.Headers.ContentType?.MediaType);
        await AssertNoInternalsAsync(response, canary, key);
        Assert.Contains(
            _logs.Entries,
            entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, _store.OpenFailure));
        Assert.DoesNotContain(
            _logs.Entries,
            entry => entry.Text.Contains(key, StringComparison.Ordinal) && entry.Exception is null);
        Assert.Empty(_store.Opened);
        await AssertUploadUntouchedAsync(upload);
    }

    [Fact]
    public async Task AStorageFailureBeforeTheFirstByteIs503WithRetryAfterAndReleasesTheStream()
    {
        var (_, browser, _, asset) = await ApprovedAsync();
        using var lifetime = browser;
        var key = asset.Variants[PhotoVariant.Full].Key.Value;
        _store.ReadFailure = new IOException($"leitura interrompida em {key}");

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}?variant=full");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
        Assert.Equal(2, ProblemAssert.RetryAfterSeconds(response));
        Assert.NotEqual("image/webp", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(IOException), text, StringComparison.Ordinal);
        var opened = Assert.Single(_store.Opened);
        Assert.True(opened.Disposed, "O stream que falhou na leitura tem de ser descartado.");
    }

    private static async Task AssertNoInternalsAsync(HttpResponseMessage response, string canary, string key)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        string[] internals =
        [
            canary, key, "IOException", "UnauthorizedAccess", "HttpRequestException", "select", "media_variants",
            "System.",
        ];
        Assert.DoesNotContain(internals, leak => text.Contains(leak, StringComparison.OrdinalIgnoreCase));
    }

    private async Task AssertUploadUntouchedAsync(StoredUpload upload) =>
        Assert.Equal(
            1,
            await _media.CountAsync(
                "select count(*) from media.uploads where id = $1 and state = 'Approved'", upload.Id));

    private async Task ApplyAsync(string change, StoredUpload upload, SeededAsset asset, Guid newOwner)
    {
        switch (change)
        {
            case "asset-deleted":
                await _media.ExecuteAsync(
                    "update media.media_assets set state = 'Deleted' where id = $1", asset.AssetId);
                break;
            case "asset-rejected":
                await _media.ExecuteAsync(
                    "update media.media_assets set state = 'Rejected' where id = $1", asset.AssetId);
                break;
            case "asset-deleted-at":
                await _media.ExecuteAsync(
                    "update media.media_assets set deleted_at = now() where id = $1", asset.AssetId);
                break;
            case "upload-deleted":
                await _media.ExecuteAsync("update media.uploads set state = 'Deleted' where id = $1", upload.Id);
                break;
            case "variant-removed":
                await _media.ExecuteAsync(
                    "delete from media.media_variants where media_asset_id = $1 and kind = 'full'", asset.AssetId);
                break;
            case "variant-moved":
                await _media.ExecuteAsync(
                    "update media.media_variants set object_key = object_key || '-moved' "
                    + "where media_asset_id = $1 and kind = 'full'",
                    asset.AssetId);
                break;
            case "asset-owner-changed":
                await _media.ExecuteAsync(
                    "update media.media_assets set owner_id = $2 where id = $1", asset.AssetId, newOwner);
                break;
            case "upload-owner-changed":
                await _media.ExecuteAsync("update media.uploads set owner_id = $2 where id = $1", upload.Id, newOwner);
                break;
            case "photo-owner-changed":
                await _media.ExecuteAsync(
                    "update media.media_assets set owner_id = $2 where id = $1", asset.AssetId, newOwner);
                await _media.ExecuteAsync("update media.uploads set owner_id = $2 where id = $1", upload.Id, newOwner);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    private async Task<(TestAccount Account, Browser Browser, StoredUpload Upload, SeededAsset Asset)> ApprovedAsync()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        var browser = await _media.Identity.SignedInBrowserAsync(account);
        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        return (account, browser, upload, await _media.ApproveWithVariantsAsync(upload));
    }

    /// <summary>Storage de verdade com ganchos: falha ao abrir ou ao ler, ou mudança logo depois de abrir.</summary>
    private sealed class ProbeStore(Func<IObjectStore> inner) : IObjectStore
    {
        public Func<ObjectKey, Task>? AfterOpen { get; set; }

        public Exception? OpenFailure { get; set; }

        public Exception? ReadFailure { get; set; }

        public ConcurrentQueue<TrackedStream> Opened { get; } = new();

        public Task PutAsync(
            ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner().PutAsync(key, content, length, contentType, cancellationToken);

        public async Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            if (OpenFailure is { } failure)
            {
                throw failure;
            }

            var stream = await inner().OpenReadAsync(key, cancellationToken);
            if (stream is null)
            {
                return null;
            }

            var tracked = new TrackedStream(stream, ReadFailure);
            Opened.Enqueue(tracked);
            if (AfterOpen is { } hook)
            {
                await hook(key);
            }

            return tracked;
        }

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner().DeleteAsync(key, cancellationToken);
    }

    /// <summary>Stream que lembra se foi descartado e, se pedido, falha na primeira leitura.</summary>
    private sealed class TrackedStream(Stream inner, Exception? readFailure) : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            readFailure is null ? inner.Read(buffer, offset, count) : throw readFailure;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            readFailure is null ? await inner.ReadAsync(buffer, cancellationToken) : throw readFailure;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            Disposed = true;
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
