using System.Net;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// T-27 (INV-03, INV-06) no envio de foto (API-M01): repetir o upload com a mesma <c>Idempotency-Key</c> e os mesmos bytes
/// depois de a conta perder o direito de enviar não devolve a foto guardada. A política atual prevalece sobre o replay,
/// na fronteira e no serviço.
/// </summary>
[Trait("Acceptance", "T-27")]
public sealed class PhotoUploadReplayAfterLostAuthorizationTests : IAsyncDisposable
{
    private readonly DuoraWebFactory _factory;
    private readonly MediaScenario _media;

    public PhotoUploadReplayAfterLostAuthorizationTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString);
        _media = new MediaScenario(postgres, _factory);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("Restricted", HttpStatusCode.Forbidden, "ACCOUNT_RESTRICTED")]
    [InlineData("Suspended", HttpStatusCode.Forbidden, "ACCOUNT_SUSPENDED")]
    [InlineData("DeletionPending", HttpStatusCode.Forbidden, "ACCOUNT_RESTRICTED")]
    public async Task TheSameKeyAfterLosingTheRightToUploadIsRefusedWithoutTheStoredPhoto(
        string status, HttpStatusCode expectedStatus, string expectedCode)
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        using var browser = await _media.Identity.SignedInBrowserAsync(account);
        var bytes = MediaScenario.JpegLike();
        var key = MediaScenario.NewKey();
        using var first = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var photoId = Guid.Parse((string)(await MediaScenario.ReadObjectAsync(first))["photoId"]!);
        await _media.ExecuteAsync("update identity.users set status = $2 where id = $1", account.UserId, status);

        using var replay = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);

        var body = (await ProblemAssert.IsProblemAsync(replay, expectedStatus, expectedCode)).ToJsonString();
        Assert.DoesNotContain(photoId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(photoId.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(replay.Headers.Location);
        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        Assert.Equal(photoId, upload.Id);
        Assert.Single(await _media.ProcessingEventsAsync(upload.Id));
        Assert.Equal(1, _media.StoredObjectCount());
    }

    /// <summary>
    /// Sem a checagem da fronteira, o serviço ainda lê o estado da conta antes de procurar a chave: a mesma chave e os
    /// mesmos bytes de uma conta já restrita dão <c>AccountRestricted</c>, nunca o <c>Accepted</c> do replay.
    /// </summary>
    [Fact]
    public async Task TheServiceRefusesTheReplayOfARestrictedAccountBeforeLookingUpTheKey()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        using var browser = await _media.Identity.SignedInBrowserAsync(account);
        var bytes = MediaScenario.JpegLike();
        var key = MediaScenario.NewKey();
        using var first = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        await _media.ExecuteAsync("update identity.users set status = 'Restricted' where id = $1", account.UserId);
        Assert.True(IdempotencyKey.TryParse(key, out var parsed));

        await using var scope = _factory.Services.CreateAsyncScope();
        await using var content = new MemoryStream(bytes);
        var result = await scope.ServiceProvider.GetRequiredService<PhotoUploads>().ReceiveAsync(
            new PhotoUploadCommand(account.UserId, parsed, PhotoFormat.Jpeg, bytes.Length),
            content,
            TestContext.Current.CancellationToken);

        Assert.IsType<PhotoUploadResult.AccountRestricted>(result);
        Assert.Single(await _media.UploadsOfAsync(account.UserId));
        Assert.Equal(1, _media.StoredObjectCount());
    }
}
