using System.Net;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// T-27 (INV-03, INV-06) na exclusão de foto (API-M03): repetir o DELETE com a mesma <c>Idempotency-Key</c> e o mesmo
/// <c>If-Match</c> depois de a conta deixar de ser titular comum não devolve o recibo guardado (204 com o ETag da foto
/// excluída). A autorização ao vivo de <c>PhotoOwnerAccess</c> roda antes de procurar o recibo.
/// </summary>
[Trait("Acceptance", "T-27")]
public sealed class PhotoDeletionReplayAfterLostAuthorizationTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DuoraWebFactory _factory;
    private readonly MediaScenario _media;

    public PhotoDeletionReplayAfterLostAuthorizationTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString);
        _media = new MediaScenario(postgres, _factory);
    }

    public enum Loss
    {
        /// <summary>O e-mail deixou de estar confirmado: só conta confirmada age sobre as próprias fotos.</summary>
        EmailNoLongerConfirmed,

        /// <summary>A conta entrou em exclusão: deixa de agir sobre as próprias fotos.</summary>
        DeletionPending,
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(Loss.EmailNoLongerConfirmed, HttpStatusCode.Forbidden, "FORBIDDEN")]
    [InlineData(Loss.DeletionPending, HttpStatusCode.Forbidden, "FORBIDDEN")]
    public async Task TheSameKeyAfterLosingOwnershipRightsIsRefusedWithoutTheStoredReceipt(
        Loss loss, HttpStatusCode expectedStatus, string expectedCode)
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var photoId = await UploadApprovedAsync(browser, account);
        var tag = await ETagAsync(browser, photoId);
        var key = MediaScenario.NewKey();
        using var first = await PhotoDeletionTests.DeleteAsync(browser, photoId, tag, key);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        await LoseAuthorizationAsync(loss, account);

        using var replay = await PhotoDeletionTests.DeleteAsync(browser, photoId, tag, key);

        var body = (await ProblemAssert.IsProblemAsync(replay, expectedStatus, expectedCode)).ToJsonString();
        Assert.DoesNotContain(photoId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(photoId.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(replay.Headers.ETag);
        await AssertSingleEffectAsync(photoId);
    }

    /// <summary>
    /// Sem o pipeline HTTP, o serviço ainda autoriza antes de ler o recibo: a mesma chave, foto e versão de uma conta em
    /// exclusão dão <c>Forbidden</c>, nunca o <c>Deleted</c> do replay.
    /// </summary>
    [Fact]
    public async Task TheServiceRefusesTheReplayOfAFormerOwnerBeforeLookingUpTheReceipt()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var photoId = await UploadApprovedAsync(browser, account);
        var tag = await ETagAsync(browser, photoId);
        var version = long.Parse(tag.Trim('"').Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
        var key = MediaScenario.NewKey();
        var session = await MfaScenario.ReadSessionIdAsync(browser);
        using var first = await PhotoDeletionTests.DeleteAsync(browser, photoId, tag, key);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        await LoseAuthorizationAsync(Loss.DeletionPending, account);
        Assert.True(IdempotencyKey.TryParse(key, out var parsed));

        await using var scope = _factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<PhotoDeletions>()
            .DeleteAsync(account.UserId, session, photoId, version, parsed, Ct);

        Assert.IsType<PhotoDeletionResult.Forbidden>(result);
        await AssertSingleEffectAsync(photoId);
    }

    private async Task LoseAuthorizationAsync(Loss loss, TestAccount account)
    {
        switch (loss)
        {
            case Loss.EmailNoLongerConfirmed:
                await _media.ExecuteAsync("update identity.users set email_confirmed = false where id = $1", account.UserId);
                break;
            case Loss.DeletionPending:
                await _media.ExecuteAsync("update identity.users set status = 'DeletionPending' where id = $1", account.UserId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(loss), loss, "Perda de autorização sem cenário.");
        }
    }

    private async Task AssertSingleEffectAsync(Guid photoId)
    {
        Assert.Equal(1, await _media.CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", photoId));
        Assert.Equal(1, await _media.CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", photoId));
        Assert.Equal(1, await _media.CountAsync(
            "select count(*) from ops.audit_events where action = 'media.photo_deleted' and target_id = $1", photoId));
    }

    private async Task<(TestAccount Account, Browser Browser)> SignedInAsync()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        return (account, await _media.Identity.SignedInBrowserAsync(account));
    }

    private async Task<Guid> UploadApprovedAsync(Browser browser, TestAccount account)
    {
        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var photoId = Guid.Parse((string)(await MediaScenario.ReadObjectAsync(response))["photoId"]!);
        await _media.ApproveWithVariantsAsync(Assert.Single(await _media.UploadsOfAsync(account.UserId), u => u.Id == photoId));
        return photoId;
    }

    private static async Task<string> ETagAsync(Browser browser, Guid photoId)
    {
        using var status = await browser.GetAsync($"/api/v1/me/photos/{photoId}/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        return status.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Status sem ETag.");
    }
}
