using System.Net;
using System.Text;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// API-M03 (spec 8, contrato docs/api/media-review.md): exclusão lógica pelo titular com If-Match forte da foto e
/// Idempotency-Key, tombstone e obrigação de limpeza física na mesma transação. Alheio e inexistente dão o mesmo 404.
/// </summary>
[Trait("Acceptance", "T-26")]
public sealed class PhotoDeletionTests : IAsyncDisposable
{
    private readonly CapturingLoggerProvider _logs = new();
    private readonly PostgresFixture _postgres;
    private readonly DuoraWebFactory _factory;
    private readonly MediaScenario _media;

    public PhotoDeletionTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs);
        _media = new MediaScenario(postgres, _factory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task TheStatusCarriesTheStrongPhotoETag()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);

        using var status = await browser.GetAsync($"/api/v1/me/photos/{upload.Id}/status");

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal($"\"photo-{upload.Id:N}-2\"", status.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task TheOwnerDeletesAnApprovedPhotoAndTheDerivativesBecomeACleanupObligation()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        var asset = await _media.ApproveWithVariantsAsync(upload);
        var tag = await ETagAsync(browser, upload.Id);

        using var response = await DeleteAsync(browser, upload.Id, tag, MediaScenario.NewKey());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal($"\"photo-{upload.Id:N}-4\"", response.Headers.ETag?.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        var stored = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        Assert.Equal("Deleted", stored.State);
        Assert.Null(stored.RejectionCode);
        Assert.Equal(4, await _media.CountAsync("select version from media.uploads where id = $1", upload.Id));
        Assert.Equal(1, await _media.CountAsync(
            "select count(*) from media.media_assets where id = $1 and state = 'Deleted' and deleted_at is not null and version = 2",
            asset.AssetId));
        Assert.Equal(1, await _media.CountAsync(
            "select count(*) from media.photo_tombstones where photo_id = $1 and owner_id = $2 and deleted_version = 4",
            upload.Id,
            account.UserId));
        // Três derivados mais o original, que não tinha obrigação de limpeza própria pendente.
        Assert.Equal(4, await _media.CountAsync("select count(*) from media.photo_deletion_cleanup where photo_id = $1", upload.Id));
        foreach (var key in asset.Variants.Values.Select(v => v.Key.Value).Append(upload.QuarantineKey))
        {
            Assert.Equal(1, await _media.CountAsync("select count(*) from media.photo_deletion_cleanup where object_key = $1", key));
        }

        Assert.Equal(1, await _media.CountAsync(
            "select count(*) from ops.audit_events where action = 'media.photo_deleted' and target_type = 'photo' and target_id = $1 and actor_id = $2",
            upload.Id,
            account.UserId));
        using var status = await browser.GetAsync($"/api/v1/me/photos/{upload.Id}/status");
        Assert.Equal("Deleted", (string?)(await MediaScenario.ReadObjectAsync(status))["status"]);
        Assert.Equal(response.Headers.ETag?.ToString(), status.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task ARejectedPhotoIsDeletedAndLosesItsRejectionCode()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ExecuteAsync(
            "update media.uploads set state = 'Rejected', rejection_code = 'ANIMATED', version = version + 1 where id = $1",
            upload.Id);

        using var response = await DeleteAsync(browser, upload.Id, await ETagAsync(browser, upload.Id), MediaScenario.NewKey());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        Assert.Equal("Deleted", stored.State);
        Assert.Null(stored.RejectionCode);
        Assert.Equal(1, await _media.CountAsync(
            "select count(*) from media.photo_deletion_cleanup where photo_id = $1 and object_key = $2",
            upload.Id,
            upload.QuarantineKey));
    }

    [Fact]
    public async Task AnOriginalWithAPendingCleanupObligationIsLeftToThatObligation()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);
        await _media.ExecuteAsync(
            "insert into media.original_cleanup (upload_id, object_key, created_at, available_at) values ($1, $2, now(), now())",
            upload.Id,
            upload.QuarantineKey);

        using var response = await DeleteAsync(browser, upload.Id, await ETagAsync(browser, upload.Id), MediaScenario.NewKey());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(3, await _media.CountAsync("select count(*) from media.photo_deletion_cleanup where photo_id = $1", upload.Id));
        Assert.Equal(1, await _media.CountAsync("select count(*) from media.original_cleanup where upload_id = $1", upload.Id));
    }

    [Fact]
    public async Task SomeoneElsesPhotoAndAMissingPhotoAnswerTheSame404AndChangeNothing()
    {
        var (owner, ownerBrowser) = await SignedInAsync();
        using var lifetime = ownerBrowser;
        var upload = await UploadAsync(ownerBrowser, owner);
        var asset = await _media.ApproveWithVariantsAsync(upload);
        var tag = await ETagAsync(ownerBrowser, upload.Id);
        var (_, stranger) = await SignedInAsync();
        using var strangerLifetime = stranger;

        using var foreign = await DeleteAsync(stranger, upload.Id, tag, MediaScenario.NewKey());
        var missingId = Guid.NewGuid();
        using var missing = await DeleteAsync(stranger, missingId, $"\"photo-{missingId:N}-2\"", MediaScenario.NewKey());

        await ProblemAssert.IsProblemAsync(foreign, HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.IsProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(await ProblemAssert.BodyWithoutTraceIdAsync(missing), await ProblemAssert.BodyWithoutTraceIdAsync(foreign));
        var foreignBody = await foreign.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(upload.Id.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(upload.Id.ToString("N"), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.Null(foreign.Headers.ETag);
        var stored = Assert.Single(await _media.UploadsOfAsync(owner.UserId));
        Assert.Equal("Approved", stored.State);
        Assert.Equal(3, await _media.CountAsync("select version from media.uploads where id = $1", upload.Id));
        Assert.Equal(1, await _media.CountAsync("select count(*) from media.media_assets where id = $1 and state = 'Approved'", asset.AssetId));
        Assert.Equal(0, await _media.CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", upload.Id));
        Assert.Equal(0, await _media.CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", upload.Id));
    }

    [Fact]
    public async Task RepeatingTheSameKeyReturnsTheConfirmedResultWithoutASecondEffect()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);
        var tag = await ETagAsync(browser, upload.Id);
        var key = MediaScenario.NewKey();

        using var first = await DeleteAsync(browser, upload.Id, tag, key);
        using var again = await DeleteAsync(browser, upload.Id, tag, key);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(first.Headers.ETag?.ToString(), again.Headers.ETag?.ToString());
        Assert.Equal(4, await _media.CountAsync("select version from media.uploads where id = $1", upload.Id));
        Assert.Equal(1, await _media.CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", upload.Id));
        Assert.Equal(1, await _media.CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", upload.Id));
        Assert.Equal(1, await _media.CountAsync("select count(*) from ops.audit_events where action = 'media.photo_deleted' and target_id = $1", upload.Id));
    }

    [Fact]
    public async Task TheSameKeyForAnotherPhotoIsAConflictAndLeavesThatPhotoIntact()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var first = await UploadAsync(browser, account);
        var second = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(first);
        await _media.ApproveWithVariantsAsync(second);
        var key = MediaScenario.NewKey();
        using (var deleted = await DeleteAsync(browser, first.Id, await ETagAsync(browser, first.Id), key))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var reused = await DeleteAsync(browser, second.Id, await ETagAsync(browser, second.Id), key);

        await ProblemAssert.IsProblemAsync(reused, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        Assert.Equal(1, await _media.CountAsync("select count(*) from media.uploads where id = $1 and state = 'Approved'", second.Id));
        Assert.Equal(0, await _media.CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", second.Id));
    }

    [Fact]
    public async Task TheSameKeyWithAnotherVersionOfTheSamePhotoIsAConflict()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);
        var key = MediaScenario.NewKey();
        using (var deleted = await DeleteAsync(browser, upload.Id, await ETagAsync(browser, upload.Id), key))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var reused = await DeleteAsync(browser, upload.Id, await ETagAsync(browser, upload.Id), key);

        await ProblemAssert.IsProblemAsync(reused, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task AnAlreadyDeletedPhotoAnswers204WithItsCurrentTagAnd412WithAStaleOne()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);
        var stale = await ETagAsync(browser, upload.Id);
        using (var deleted = await DeleteAsync(browser, upload.Id, stale, MediaScenario.NewKey()))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var current = await ETagAsync(browser, upload.Id);
        using var repeated = await DeleteAsync(browser, upload.Id, current, MediaScenario.NewKey());
        using var outdated = await DeleteAsync(browser, upload.Id, stale, MediaScenario.NewKey());

        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(current, repeated.Headers.ETag?.ToString());
        await ProblemAssert.IsProblemAsync(outdated, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        Assert.Equal(4, await _media.CountAsync("select version from media.uploads where id = $1", upload.Id));
        Assert.Equal(1, await _media.CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", upload.Id));
        Assert.Equal(4, await _media.CountAsync("select count(*) from media.photo_deletion_cleanup where photo_id = $1", upload.Id));
        Assert.Equal(1, await _media.CountAsync("select count(*) from ops.audit_events where action = 'media.photo_deleted' and target_id = $1", upload.Id));
    }

    [Fact]
    public async Task AStaleVersionIs412WithoutEffect()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        var stale = await ETagAsync(browser, upload.Id);
        await _media.ApproveWithVariantsAsync(upload);

        using var response = await DeleteAsync(browser, upload.Id, stale, MediaScenario.NewKey());

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        Assert.Equal(1, await _media.CountAsync("select count(*) from media.uploads where id = $1 and state = 'Approved' and version = 3", upload.Id));
        Assert.Equal(0, await _media.CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", upload.Id));
    }

    [Fact]
    public async Task WithoutIfMatchTheAnswerIs428()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);

        using var response = await DeleteAsync(browser, upload.Id, null, MediaScenario.NewKey());

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.PreconditionRequired, "PRECONDITION_REQUIRED");
        Assert.Equal("Approved", Assert.Single(await _media.UploadsOfAsync(account.UserId)).State);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("W/\"photo-{0}-3\"")]
    [InlineData("\"photo-{0}-3\", \"photo-{0}-4\"")]
    [InlineData("\"case-{0}-3\"")]
    [InlineData("\"3\"")]
    [InlineData("\"photo-{0}-03\"")]
    public async Task AnythingButTheExactStrongPhotoTagIsRefused(string template)
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);

        using var response = await DeleteAsync(browser, upload.Id, string.Format(System.Globalization.CultureInfo.InvariantCulture, template, upload.Id.ToString("N")), MediaScenario.NewKey());

        Assert.True(
            response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.PreconditionRequired,
            $"If-Match {template} recebeu {(int)response.StatusCode}.");
        Assert.Equal("Approved", Assert.Single(await _media.UploadsOfAsync(account.UserId)).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chave com espaço")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public async Task AMissingOrInvalidIdempotencyKeyIs400(string? key)
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);

        using var response = await DeleteAsync(browser, upload.Id, await ETagAsync(browser, upload.Id), key);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_INVALID");
        Assert.Equal("Approved", Assert.Single(await _media.UploadsOfAsync(account.UserId)).State);
    }

    [Fact]
    public async Task ABodyIsRefused()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);
        var tag = await ETagAsync(browser, upload.Id);

        using var response = await browser.SendDeleteWithBodyAsync(
            $"/api/v1/me/photos/{upload.Id}",
            new StringContent("{\"ownerId\":\"x\"}", Encoding.UTF8, "application/json"),
            Headers(tag, MediaScenario.NewKey()));

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("Approved", Assert.Single(await _media.UploadsOfAsync(account.UserId)).State);
    }

    [Theory]
    [InlineData("Quarantined")]
    [InlineData("Processing")]
    [InlineData("AwaitingReview")]
    public async Task APhotoStillInProcessingOrReviewIsAStateConflict(string state)
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        if (state != "Quarantined")
        {
            await _media.ExecuteAsync("update media.uploads set state = $2, version = version + 1 where id = $1", upload.Id, state);
        }

        using var response = await DeleteAsync(browser, upload.Id, await ETagAsync(browser, upload.Id), MediaScenario.NewKey());

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "PHOTO_DELETE_STATE_CONFLICT");
        Assert.Equal(state, Assert.Single(await _media.UploadsOfAsync(account.UserId)).State);
        Assert.Equal(0, await _media.CountAsync("select count(*) from media.photo_deletion_commands where photo_id = $1", upload.Id));
    }

    [Fact]
    public async Task ARejectedPhotoWithAPrivateReviewCandidateStillRetainedIsAStateConflict()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ExecuteAsync(
            "update media.uploads set state = 'Rejected', rejection_code = 'PROCESSING_LIMIT', version = version + 1 where id = $1",
            upload.Id);
        await _media.ExecuteAsync(
            """
            insert into media.review_candidates (generation, upload_id, upload_version, detected_type, width, height,
              created_at, prepare_deadline, cleanup_available_at)
            values ($1, $2, 1, 'image/jpeg', 10, 10, now(), now() + interval '15 seconds', now())
            """,
            Guid.NewGuid(),
            upload.Id);

        using var response = await DeleteAsync(browser, upload.Id, await ETagAsync(browser, upload.Id), MediaScenario.NewKey());

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "PHOTO_DELETE_STATE_CONFLICT");
        Assert.Equal("Rejected", Assert.Single(await _media.UploadsOfAsync(account.UserId)).State);
    }

    [Fact]
    public async Task ASuspendedOwnerCanStillDeleteTheirOwnPhoto()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);
        var tag = await ETagAsync(browser, upload.Id);
        await _media.ExecuteAsync("update identity.users set status = 'Suspended' where id = $1", account.UserId);

        using var response = await DeleteAsync(browser, upload.Id, tag, MediaScenario.NewKey());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Deleted", Assert.Single(await _media.UploadsOfAsync(account.UserId)).State);
    }

    [Fact]
    public async Task WithoutASessionTheAnswerIs401()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);
        using var anonymous = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/photos/{upload.Id}");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"photo-{upload.Id:N}-3\"");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", MediaScenario.NewKey());

        using var response = await anonymous.SendAsync(request, Ct);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"Recebeu {(int)response.StatusCode}.");
        Assert.Equal("Approved", Assert.Single(await _media.UploadsOfAsync(account.UserId)).State);
    }

    [Fact]
    public async Task ConcurrentDeletesWithTheSameKeyProduceOneEffectAndTheSameAnswer()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);
        var tag = await ETagAsync(browser, upload.Id);
        var key = MediaScenario.NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DeleteAsync(browser, upload.Id, tag, key)));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
            Assert.Single(responses.Select(response => response.Headers.ETag?.ToString()).Distinct());
            Assert.Equal(1, await _media.CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", upload.Id));
            Assert.Equal(1, await _media.CountAsync("select count(*) from ops.audit_events where action = 'media.photo_deleted' and target_id = $1", upload.Id));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConcurrentDeletesWithDifferentKeysLetOnlyOneWinTheVersion()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);
        var tag = await ETagAsync(browser, upload.Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DeleteAsync(browser, upload.Id, tag, MediaScenario.NewKey())));

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
            Assert.All(
                responses.Where(response => response.StatusCode != HttpStatusCode.NoContent),
                response => Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode));
            Assert.Equal(1, await _media.CountAsync("select count(*) from media.photo_tombstones where photo_id = $1", upload.Id));
            Assert.Equal(4, await _media.CountAsync("select version from media.uploads where id = $1", upload.Id));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task WithUploadsDisabledTheRouteAnswers503AndChangesNothing()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        await _media.ApproveWithVariantsAsync(upload);
        var tag = await ETagAsync(browser, upload.Id);
        await using var disabled = new DuoraWebFactory(_postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?> { ["Media:Review"] = null, ["Media:Storage:Provider"] = null },
        };
        var identity = new IdentityScenario(_postgres.ConnectionString, disabled);
        using var disabledBrowser = await identity.SignedInBrowserAsync(account);

        using var response = await DeleteAsync(disabledBrowser, upload.Id, tag, MediaScenario.NewKey());

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "MEDIA_UPLOADS_DISABLED");
        Assert.Equal("Approved", Assert.Single(await _media.UploadsOfAsync(account.UserId)).State);
    }

    [Fact]
    public async Task ResponsesAndLogsNeverCarryStorageKeysTheIdempotencyKeyOrStackTraces()
    {
        var (account, browser) = await SignedInAsync();
        using var lifetime = browser;
        var upload = await UploadAsync(browser, account);
        var asset = await _media.ApproveWithVariantsAsync(upload);
        var tag = await ETagAsync(browser, upload.Id);
        var key = "CANARY-" + Guid.NewGuid().ToString("N");

        using var deleted = await DeleteAsync(browser, upload.Id, tag, key);
        using var conflict = await DeleteAsync(browser, upload.Id, await ETagAsync(browser, upload.Id), key);
        using var stale = await DeleteAsync(browser, upload.Id, tag, MediaScenario.NewKey());

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        foreach (var response in new[] { deleted, conflict, stale })
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers).Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
            foreach (var forbidden in new[] { "derivatives/", "quarantine/", key, "Exception", "   at ", "Npgsql", "Duora." })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
                Assert.DoesNotContain(forbidden, headers, StringComparison.Ordinal);
            }
        }

        foreach (var (objectKey, _) in asset.Variants.Values)
        {
            Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(objectKey.Value, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(key, StringComparison.Ordinal));
    }

    private async Task<(TestAccount Account, Browser Browser)> SignedInAsync()
    {
        var account = await _media.Identity.CreateConfirmedAccountAsync();
        return (account, await _media.Identity.SignedInBrowserAsync(account));
    }

    private async Task<StoredUpload> UploadAsync(Browser browser, TestAccount account)
    {
        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var photoId = Guid.Parse((string)(await MediaScenario.ReadObjectAsync(response))["photoId"]!);
        return Assert.Single(await _media.UploadsOfAsync(account.UserId), upload => upload.Id == photoId);
    }

    private static async Task<string> ETagAsync(Browser browser, Guid photoId)
    {
        using var status = await browser.GetAsync($"/api/v1/me/photos/{photoId}/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        return status.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Status sem ETag.");
    }

    internal static Task<HttpResponseMessage> DeleteAsync(Browser browser, Guid photoId, string? ifMatch, string? key) =>
        browser.DeleteAsync($"/api/v1/me/photos/{photoId}", Headers(ifMatch, key));

    private static MutationHeaders Headers(string? ifMatch, string? key)
    {
        var extra = new Dictionary<string, string>();
        if (ifMatch is not null)
        {
            extra["If-Match"] = ifMatch;
        }

        if (key is not null)
        {
            extra["Idempotency-Key"] = key;
        }

        return new MutationHeaders(Extra: extra);
    }
}
