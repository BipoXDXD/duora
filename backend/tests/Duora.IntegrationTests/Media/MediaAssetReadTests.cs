using System.Net;
using Duora.Core.Media;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// API-M04 (spec 8; TM-25, TM-44): só o derivado de um asset <c>Approved</c>, transmitido pela API com
/// <c>no-store</c>, e a autorização relida a cada pedido. Alheio, inexistente, não aprovado e excluído dão o mesmo 404.
/// </summary>
[Trait("Acceptance", "T-26")]
public sealed class MediaAssetReadTests : IAsyncDisposable
{
    private readonly CapturingLoggerProvider _logs = new();
    private readonly PostgresFixture _postgres;
    private readonly DuoraWebFactory _factory;
    private readonly MediaScenario _media;

    public MediaAssetReadTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs);
        _media = new MediaScenario(postgres, _factory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task TheOwnerReadsTheFullDerivativeByDefaultWithoutCaching()
    {
        var (_, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(asset.Variants[PhotoVariant.Full].Bytes, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", string.Join(',', response.Headers.GetValues("X-Content-Type-Options")));
        Assert.False(response.Headers.Contains("Accept-Ranges"));
        Assert.Null(response.Headers.ETag);
    }

    [Theory]
    [InlineData("thumb", PhotoVariant.Thumb)]
    [InlineData("card", PhotoVariant.Card)]
    [InlineData("full", PhotoVariant.Full)]
    public async Task EachVariantIsServedFromItsOwnDerivative(string name, PhotoVariant variant)
    {
        var (_, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}?variant={name}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(asset.Variants[variant].Bytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("FULL")]
    [InlineData("")]
    [InlineData("full&variant=thumb")]
    public async Task AnUnknownVariantIs400(string name)
    {
        var (_, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}?variant={name}");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task SomeoneElsesAssetAndAMissingAssetAnswerTheSame404WithoutBytes()
    {
        var (_, owner, asset) = await ApprovedAsync();
        using var ownerLifetime = owner;
        var stranger = await _media.Identity.CreateConfirmedAccountAsync();
        using var strangerBrowser = await _media.Identity.SignedInBrowserAsync(stranger);

        using var foreign = await strangerBrowser.GetAsync($"/api/v1/media/{asset.AssetId}");
        using var missing = await strangerBrowser.GetAsync($"/api/v1/media/{Guid.NewGuid()}");

        await ProblemAssert.IsProblemAsync(foreign, HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.IsProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(await ProblemAssert.BodyWithoutTraceIdAsync(missing), await ProblemAssert.BodyWithoutTraceIdAsync(foreign));
        Assert.NotEqual("image/webp", foreign.Content.Headers.ContentType?.MediaType);
        var body = await foreign.Content.ReadAsByteArrayAsync(Ct);
        Assert.DoesNotContain(asset.AssetId.ToString(), System.Text.Encoding.UTF8.GetString(body), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Deleted")]
    public async Task AnAssetThatIsNoLongerApprovedIs404(string state)
    {
        var (_, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;
        await _media.ExecuteAsync("update media.media_assets set state = $2 where id = $1", asset.AssetId, state);

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task AnApprovedAssetWhoseUploadLeftApprovedIs404()
    {
        var (account, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;
        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        await _media.ExecuteAsync("update media.uploads set state = 'Deleted' where id = $1", upload.Id);

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task TheOldUrlStopsWorkingRightAfterTheOwnerDeletesThePhoto()
    {
        var (account, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;
        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        using (var before = await browser.GetAsync($"/api/v1/media/{asset.AssetId}?variant=thumb"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using (var deleted = await PhotoDeletionTests.DeleteAsync(browser, upload.Id, $"\"photo-{upload.Id:N}-3\"", MediaScenario.NewKey()))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var after = await browser.GetAsync($"/api/v1/media/{asset.AssetId}?variant=thumb");

        await ProblemAssert.IsProblemAsync(after, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.True(await _media.ObjectExistsAsync(asset.Variants[PhotoVariant.Thumb].Key), "A limpeza física é do coletor, não da leitura.");
    }

    [Fact]
    public async Task AMissingDerivativeFileIs404WithoutLeakingTheKey()
    {
        var (_, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;
        var key = asset.Variants[PhotoVariant.Full].Key;
        await _media.Store.DeleteAsync(key, Ct);

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(key.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// Derivados legados (<c>derivatives/&lt;token&gt;-&lt;kind&gt;</c>) nunca tiveram digest persistido. Recusá-los
    /// tiraria do ar as fotos que já existem, então seguem entregues, com um registro que permite contar quantos ainda
    /// passam sem conferência. Os promovidos são conferidos em <c>OwnerMediaReadDigestTests</c>.
    /// </summary>
    [Fact]
    public async Task ALegacyDerivativeWithoutADigestIsStillServedAndCounted()
    {
        var (_, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}?variant=card");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(asset.Variants[PhotoVariant.Card].Bytes, await response.Content.ReadAsByteArrayAsync(Ct));
        var entry = Assert.Single(_logs.Entries, entry => entry.Category == "Duora.Infrastructure.Media.OwnerMediaReads"
            && entry.Text.Contains(asset.AssetId.ToString(), StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, entry.Level);
        Assert.Contains("sem digest", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(asset.Variants[PhotoVariant.Card].Key.Value, entry.Text, StringComparison.Ordinal);
    }

    /// <summary>Uma chave no formato promovido não vira legado só porque o asset não tem promoção ligada.</summary>
    [Fact]
    public async Task APromotedFormatKeyOnAnAssetWithoutAPromotionIs404()
    {
        var (_, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;
        var key = ObjectKey.ForPromotedDerivative(Guid.NewGuid(), PhotoVariant.Full);
        var bytes = asset.Variants[PhotoVariant.Full].Bytes;
        await _media.Store.PutAsync(key, new MemoryStream(bytes), bytes.Length, "image/webp", Ct);
        await _media.ExecuteAsync("update media.media_variants set object_key = $2 where media_asset_id = $1 and kind = 'full'",
            asset.AssetId, key.Value);

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        var entry = Assert.Single(_logs.Entries, entry => entry.Category == "Duora.Infrastructure.Media.OwnerMediaReads");
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        Assert.DoesNotContain(key.Value, entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASessionTheAnswerIs401()
    {
        var (_, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.GetAsync(new Uri($"/api/v1/media/{asset.AssetId}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual("image/webp", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task WithUploadsDisabledTheRouteAnswers503()
    {
        var (account, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;
        await using var disabled = new DuoraWebFactory(_postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?> { ["Media:Review"] = null, ["Media:Storage:Provider"] = null },
        };
        var identity = new IdentityScenario(_postgres.ConnectionString, disabled);
        using var disabledBrowser = await identity.SignedInBrowserAsync(account);

        using var response = await disabledBrowser.GetAsync($"/api/v1/media/{asset.AssetId}");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "MEDIA_UPLOADS_DISABLED");
    }

    [Fact]
    public async Task ResponsesNeverCarryStorageKeys()
    {
        var (_, browser, asset) = await ApprovedAsync();
        using var lifetime = browser;

        using var response = await browser.GetAsync($"/api/v1/media/{asset.AssetId}");

        var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers).Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
        Assert.DoesNotContain("derivatives", headers, StringComparison.OrdinalIgnoreCase);
        foreach (var (key, _) in asset.Variants.Values)
        {
            Assert.DoesNotContain(key.Value[(key.Value.IndexOf('/', StringComparison.Ordinal) + 1)..], headers, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(key.Value, StringComparison.Ordinal));
        }
    }

    private async Task<(TestAccount Account, Browser Browser, SeededAsset Asset)> ApprovedAsync()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        var browser = await _media.Identity.SignedInBrowserAsync(account);
        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        return (account, browser, await _media.ApproveWithVariantsAsync(upload));
    }
}
