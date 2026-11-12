using System.Data;
using System.Net;
using Duora.Core.Identity;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Autorização viva do titular sobre as próprias fotos (<c>PhotoOwnerAccess</c>), relida no banco dentro da
/// transação de quem a consome. Quem a consome decide a resposta: a exclusão devolve <c>Forbidden</c> (403 na
/// rota) e a leitura de mídia devolve nulo (o mesmo 404 do alheio). O 403 daqui é da sessão, não do recurso: foto
/// de outra conta continua 404 (docs/api/media-review.md, API-M03). A política padrão das rotas já barra conta
/// privilegiada na borda
/// (<c>PrivilegedAccountsAreForbiddenOnTheOwnerPhotoRoutesAndNothingOfTheOwnerLeaks</c>), então estes testes
/// chamam o serviço com a sessão que a borda deixaria passar e o banco já não reconhece.
/// </summary>
[Trait("Acceptance", "T-26")]
public sealed class PhotoOwnerAccessTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _time =
        new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly MediaScenario _media;
    private readonly ReadCountingStore _store;

    public PhotoOwnerAccessTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _store = new ReadCountingStore(() => MediaStore);
        _factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Time = _time,
            TestServices = services => services.AddSingleton<IObjectStore>(_store),
        };
        _media = new MediaScenario(postgres, _factory);
    }

    private IObjectStore MediaStore => _media.Store;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task AParticipantWithALiveNormalSessionIsAuthorized()
    {
        var owner = await ApprovedPhotoAsync();

        Assert.True(await AuthorizeAsync(owner.Account.UserId, owner.SessionId));
    }

    [Theory]
    [InlineData("PendingEligibility")]
    [InlineData("Active")]
    [InlineData("Restricted")]
    [InlineData("Suspended")]
    public async Task EveryStatusThatKeepsThePhotosReachableIsAuthorized(string status)
    {
        var owner = await ApprovedPhotoAsync();
        await _media.ExecuteAsync("update identity.users set status = $2 where id = $1", owner.Account.UserId, status);

        Assert.True(await AuthorizeAsync(owner.Account.UserId, owner.SessionId));
    }

    [Theory]
    [InlineData("email-not-confirmed")]
    [InlineData("temporary-password")]
    [InlineData("pending-email")]
    [InlineData("deletion-pending")]
    [InlineData("no-role")]
    [InlineData("privileged-role-only")]
    [InlineData("administrative-session")]
    [InlineData("stale-security-stamp")]
    [InlineData("revoked-session")]
    [InlineData("idle-expired-this-instant")]
    [InlineData("absolute-expired-this-instant")]
    public async Task ASessionTheDatabaseNoLongerRecognizesIsNotAuthorized(string how)
    {
        var owner = await ApprovedPhotoAsync();
        await DisqualifyAsync(how, owner);

        Assert.False(await AuthorizeAsync(owner.Account.UserId, owner.SessionId));
    }

    [Fact]
    public async Task ASessionThatExpiresOneSecondFromNowIsStillAuthorized()
    {
        var owner = await ApprovedPhotoAsync();
        await _media.ExecuteAsync(
            "update identity.login_sessions set idle_expires_at = $2 where id = $1",
            owner.SessionId,
            _time.GetUtcNow().AddSeconds(1));

        Assert.True(await AuthorizeAsync(owner.Account.UserId, owner.SessionId));
    }

    [Fact]
    public async Task TheClockDecidesWhenTheSessionExpires()
    {
        var owner = await ApprovedPhotoAsync();
        var idle = await IdleExpiryAsync(owner.SessionId);
        _time.SetUtcNow(idle.AddTicks(-TimeSpan.TicksPerSecond));
        Assert.True(await AuthorizeAsync(owner.Account.UserId, owner.SessionId));

        _time.SetUtcNow(idle);

        Assert.False(await AuthorizeAsync(owner.Account.UserId, owner.SessionId));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AnEmptyUserOrSessionIsNeverAuthorized(bool emptyUser, bool emptySession)
    {
        var owner = await ApprovedPhotoAsync();

        var allowed = await AuthorizeAsync(
            emptyUser ? Guid.Empty : owner.Account.UserId,
            emptySession ? Guid.Empty : owner.SessionId);

        Assert.False(allowed);
    }

    [Fact]
    public async Task AnUnknownUserIsNotAuthorizedAndGetsNoGuardRow()
    {
        var owner = await ApprovedPhotoAsync();
        var stranger = Guid.NewGuid();

        Assert.False(await AuthorizeAsync(stranger, owner.SessionId));
        Assert.Equal(
            0,
            await _media.CountAsync("select count(*) from safety.user_guards where user_id = $1", stranger));
    }

    [Fact]
    public async Task ASessionThatIsNotOfThatAccountIsNotAuthorized()
    {
        var owner = await ApprovedPhotoAsync();
        var other = await ApprovedPhotoAsync();
        // A sessão alheia leva o security stamp do titular: só o vínculo da sessão com a conta a separa dele.
        await _media.ExecuteAsync(
            """
            update identity.login_sessions
            set security_stamp = (select security_stamp from identity.users where id = $1)
            where id = $2
            """,
            owner.Account.UserId,
            other.SessionId);

        Assert.False(await AuthorizeAsync(owner.Account.UserId, other.SessionId));
        Assert.False(await AuthorizeAsync(owner.Account.UserId, Guid.NewGuid()));
        Assert.True(await AuthorizeAsync(owner.Account.UserId, owner.SessionId));
    }

    [Fact]
    public async Task ADeletionFromASessionTheDatabaseNoLongerRecognizesIsForbiddenAndChangesNothing()
    {
        var owner = await ApprovedPhotoAsync();
        await DisqualifyAsync("revoked-session", owner);

        var result = await DeleteAsync(owner);

        Assert.IsType<PhotoDeletionResult.Forbidden>(result);
        await AssertPhotoUntouchedAsync(owner);
        await _media.ExecuteAsync(
            "update identity.login_sessions set revoked_at = null where id = $1", owner.SessionId);
        Assert.IsType<PhotoDeletionResult.Deleted>(await DeleteAsync(owner));
    }

    [Fact]
    public async Task APhotoOfAnotherAccountIsNotFoundForAQualifiedSessionAndNeverForbidden()
    {
        var owner = await ApprovedPhotoAsync();
        var stranger = await ApprovedPhotoAsync();

        var result = await DeleteAsync(owner, asOwnerOf: stranger);

        Assert.IsType<PhotoDeletionResult.NotFound>(result);
        await AssertPhotoUntouchedAsync(owner);
        await AssertPhotoUntouchedAsync(stranger);
    }

    [Fact]
    public async Task AMediaReadFromASessionTheDatabaseNoLongerRecognizesIsNullAndNeverOpensTheStorage()
    {
        var owner = await ApprovedPhotoAsync();
        await DisqualifyAsync("revoked-session", owner);
        var opensBefore = _store.Opens;

        await using (var refused = await OpenAsync(owner))
        {
            Assert.Null(refused);
        }

        Assert.Equal(opensBefore, _store.Opens);
        await _media.ExecuteAsync(
            "update identity.login_sessions set revoked_at = null where id = $1", owner.SessionId);
        await using var served = await OpenAsync(owner);
        Assert.NotNull(served);
        using var copy = new MemoryStream();
        await served.Content.CopyToAsync(copy, Ct);
        Assert.Equal(owner.Asset.Variants[PhotoVariant.Full].Bytes, copy.ToArray());
    }

    private async Task<bool> AuthorizeAsync(Guid userId, Guid sessionId)
    {
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(_postgres.ConnectionString);
        await using var db = new DuoraDbContext(options.Options);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct);
        var allowed = await PhotoOwnerAccess.AuthorizeAsync(db, _time, new PhotoReviewActor(userId, sessionId), Ct);
        await transaction.CommitAsync(Ct);
        return allowed;
    }

    private async Task<PhotoDeletionResult> DeleteAsync(OwnedPhoto photo, OwnedPhoto? asOwnerOf = null)
    {
        var actor = asOwnerOf ?? photo;
        Assert.True(IdempotencyKey.TryParse(MediaScenario.NewKey(), out var key));
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhotoDeletions>()
            .DeleteAsync(actor.Account.UserId, actor.SessionId, photo.Upload.Id, photo.Version, key, Ct);
    }

    private async Task<AuthorizedMedia?> OpenAsync(OwnedPhoto photo)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OwnerMediaReads>()
            .OpenAsync(photo.Account.UserId, photo.SessionId, photo.Asset.AssetId, PhotoVariant.Full, Ct);
    }

    private async Task DisqualifyAsync(string how, OwnedPhoto owner)
    {
        var user = owner.Account.UserId;
        var session = owner.SessionId;
        var now = _time.GetUtcNow();
        switch (how)
        {
            case "email-not-confirmed":
                await _media.ExecuteAsync("update identity.users set email_confirmed = false where id = $1", user);
                break;
            case "temporary-password":
                await _media.ExecuteAsync(
                    "update identity.users set temporary_password_expires_at = $2 where id = $1", user, now.AddDays(1));
                break;
            case "pending-email":
                await _media.ExecuteAsync("update identity.users set status = 'PendingEmail' where id = $1", user);
                break;
            case "deletion-pending":
                await _media.ExecuteAsync("update identity.users set status = 'DeletionPending' where id = $1", user);
                break;
            case "no-role":
                await _media.ExecuteAsync("delete from identity.user_roles where user_id = $1", user);
                break;
            case "privileged-role-only":
                await _media.ExecuteAsync("delete from identity.user_roles where user_id = $1", user);
                await _media.Identity.GrantRoleAsync(user, AccountRoles.Moderator);
                break;
            case "administrative-session":
                await _media.ExecuteAsync(
                    "update identity.login_sessions set policy = 'Administrative' where id = $1", session);
                break;
            case "stale-security-stamp":
                await _media.ExecuteAsync(
                    "update identity.users set security_stamp = gen_random_uuid()::text where id = $1", user);
                break;
            case "revoked-session":
                await _media.ExecuteAsync(
                    "update identity.login_sessions set revoked_at = $2 where id = $1", session, now);
                break;
            case "idle-expired-this-instant":
                await _media.ExecuteAsync(
                    "update identity.login_sessions set idle_expires_at = $2 where id = $1", session, now);
                break;
            case "absolute-expired-this-instant":
                // A janela exige idle <= absolute (ck_login_sessions_window): expirar a absoluta expira as duas.
                await _media.ExecuteAsync(
                    "update identity.login_sessions set idle_expires_at = $2, absolute_expires_at = $2 where id = $1",
                    session,
                    now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(how), how, null);
        }
    }

    private async Task AssertPhotoUntouchedAsync(OwnedPhoto photo)
    {
        var id = photo.Upload.Id;
        Assert.Equal(1, await _media.CountAsync(
            "select count(*) from media.uploads where id = $1 and state = 'Approved' and version = $2",
            id,
            photo.Version));
        Assert.Equal(1, await _media.CountAsync(
            "select count(*) from media.media_assets where id = $1 and state = 'Approved' and deleted_at is null",
            photo.Asset.AssetId));
        Assert.Equal(0, await _media.CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", id));
        Assert.Equal(
            0, await _media.CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", id));
        Assert.Equal(
            0, await _media.CountAsync("select count(*) from media.photo_deletion_cleanup where photo_id = $1", id));
        Assert.Equal(0, await _media.CountAsync(
            "select count(*) from ops.audit_events where action = 'media.photo_deleted' and target_id = $1", id));
        foreach (var (key, _) in photo.Asset.Variants.Values)
        {
            Assert.True(await _media.ObjectExistsAsync(key), "Nenhum derivado pode sair do storage.");
        }
    }

    private async Task<DateTimeOffset> IdleExpiryAsync(Guid sessionId)
    {
        var text = await _media.Identity.ScalarTextAsync(
            """
            select to_char(idle_expires_at at time zone 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')
            from identity.login_sessions where id = $1
            """,
            sessionId);
        return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<OwnedPhoto> ApprovedPhotoAsync()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        using (var browser = await _media.Identity.SignedInBrowserAsync(account))
        {
            using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        var asset = await _media.ApproveWithVariantsAsync(upload);
        var session = Guid.Parse(await _media.Identity.ScalarTextAsync(
            "select id::text from identity.login_sessions where user_id = $1 and revoked_at is null",
            account.UserId));
        var version = await _media.CountAsync("select version from media.uploads where id = $1", upload.Id);
        return new OwnedPhoto(account, session, upload, asset, version);
    }

    private sealed record OwnedPhoto(
        TestAccount Account, Guid SessionId, StoredUpload Upload, SeededAsset Asset, long Version);

    /// <summary>Storage de verdade que só conta quantas vezes um objeto foi aberto para leitura.</summary>
    private sealed class ReadCountingStore(Func<IObjectStore> inner) : IObjectStore
    {
        private int _opens;

        public int Opens => Volatile.Read(ref _opens);

        public Task PutAsync(
            ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
            inner().PutAsync(key, content, length, contentType, cancellationToken);

        public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _opens);
            return inner().OpenReadAsync(key, cancellationToken);
        }

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner().DeleteAsync(key, cancellationToken);
    }
}
