using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Duora.IntegrationTests.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Media;

/// <summary>API-M01 e API-M02 (ADR-0025, T-16): recepção em quarentena, idempotência local, teto de pendentes e leitura de estado só pelo titular.</summary>
[Trait("Acceptance", "T-16")]
public sealed class PhotoUploadTests : IAsyncDisposable
{
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DuoraWebFactory _factory;
    private readonly MediaScenario _media;

    public PhotoUploadTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString, _logs);
        _media = new MediaScenario(postgres, _factory);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task AnUploadIsStoredInQuarantineAndQueuedForProcessing()
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        var bytes = MediaScenario.JpegLike();

        using var response = await MediaScenario.UploadAsync(browser, bytes);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await MediaScenario.ReadObjectAsync(response);
        Assert.Equal(["photoId", "status"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("Quarantined", (string?)body["status"]);
        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        Assert.Equal(upload.Id, Guid.Parse((string)body["photoId"]!));
        Assert.Equal($"/api/v1/me/photos/{upload.Id}/status", response.Headers.Location?.OriginalString);
        Assert.Equal("Quarantined", upload.State);
        Assert.Equal("image/jpeg", upload.DeclaredContentType);
        Assert.Equal(bytes.Length, upload.SizeBytes);
        Assert.Equal(SHA256.HashData(bytes), upload.ContentSha256);
        Assert.Matches("^quarantine/[0-9a-f]{32}$", upload.QuarantineKey);
        Assert.Equal(bytes, await _media.ReadQuarantineAsync(upload));
        var queued = Assert.Single(await _media.ProcessingEventsAsync(upload.Id));
        Assert.Equal("media-processing", queued.Consumer);
        Assert.Equal("Pending", queued.Status);
        var payload = JsonNode.Parse(queued.Payload)!.AsObject();
        Assert.Equal(["uploadId"], payload.Select(p => p.Key));
        Assert.Equal(upload.Id, Guid.Parse((string)payload["uploadId"]!));
    }

    [Fact]
    public async Task TheOwnerReadsTheProcessingStatus()
    {
        using var browser = await SignInAsync(await NewAccountAsync());
        var photoId = await UploadAndReadIdAsync(browser);

        using var response = await browser.GetAsync($"/api/v1/me/photos/{photoId}/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await MediaScenario.ReadObjectAsync(response);
        Assert.Equal(["photoId", "rejectionCode", "status"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(photoId, Guid.Parse((string)body["photoId"]!));
        Assert.Equal("Quarantined", (string?)body["status"]);
        Assert.Null(body["rejectionCode"]);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("Quarantined", null)]
    [InlineData("Processing", null)]
    [InlineData("Approved", null)]
    [InlineData("Rejected", "ANIMATED")]
    [InlineData("Deleted", null)]
    public async Task TheStatusIsExactlyTheStoredState(string state, string? rejectionCode)
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        var photoId = await UploadAndReadIdAsync(browser);
        await _media.ExecuteAsync(
            "update media.uploads set state = $2, rejection_code = $3 where id = $1",
            photoId,
            state,
            (object?)rejectionCode ?? DBNull.Value);

        using var response = await browser.GetAsync($"/api/v1/me/photos/{photoId}/status");

        var body = await MediaScenario.ReadObjectAsync(response);
        Assert.Equal(state, (string?)body["status"]);
        Assert.Equal(rejectionCode, (string?)body["rejectionCode"]);
    }

    [Fact]
    public async Task SomeoneElsesPhotoAndAMissingPhotoAnswerTheSame404()
    {
        using var owner = await SignInAsync(await NewAccountAsync());
        var photoId = await UploadAndReadIdAsync(owner);
        using var stranger = await SignInAsync(await NewAccountAsync());

        using var foreign = await stranger.GetAsync($"/api/v1/me/photos/{photoId}/status");
        using var missing = await stranger.GetAsync($"/api/v1/me/photos/{Guid.NewGuid()}/status");

        await ProblemAssert.IsProblemAsync(foreign, HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.IsProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(await ProblemAssert.BodyWithoutTraceIdAsync(missing), await ProblemAssert.BodyWithoutTraceIdAsync(foreign));
        Assert.DoesNotContain(photoId.ToString(), await foreign.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepeatingTheSameUploadWithTheSameKeyReturnsTheFirstPhoto()
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        var bytes = MediaScenario.JpegLike();
        var key = MediaScenario.NewKey();

        using var first = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);
        using var again = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        var firstBody = await MediaScenario.ReadObjectAsync(first);
        var againBody = await MediaScenario.ReadObjectAsync(again);
        Assert.Equal((string?)firstBody["photoId"], (string?)againBody["photoId"]);
        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        Assert.Single(await _media.ProcessingEventsAsync(upload.Id));
        Assert.Equal(1, _media.StoredObjectCount());
    }

    [Fact]
    public async Task ReusingAKeyWithOtherBytesIsAConflictWithoutSideEffects()
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        var key = MediaScenario.NewKey();
        using (await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(fill: 1), idempotencyKey: key))
        {
        }

        using var reused = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(fill: 2), idempotencyKey: key);

        await ProblemAssert.IsProblemAsync(reused, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        Assert.Single(await _media.UploadsOfAsync(account.UserId));
        Assert.Equal(1, _media.StoredObjectCount());
    }

    [Fact]
    public async Task ReusingAKeyWithTheSameBytesAsAnotherTypeIsAConflict()
    {
        using var browser = await SignInAsync(await NewAccountAsync());
        var key = MediaScenario.NewKey();
        var bytes = MediaScenario.JpegLike();
        using (await MediaScenario.UploadAsync(browser, bytes, "image/jpeg", key))
        {
        }

        using var reused = await MediaScenario.UploadAsync(browser, bytes, "image/png", key);

        await ProblemAssert.IsProblemAsync(reused, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task TheSameKeyFromAnotherAccountIsAnotherUpload()
    {
        var first = await NewAccountAsync();
        using var firstBrowser = await SignInAsync(first);
        var second = await NewAccountAsync();
        using var secondBrowser = await SignInAsync(second);
        var key = MediaScenario.NewKey();
        var bytes = MediaScenario.JpegLike();

        using var a = await MediaScenario.UploadAsync(firstBrowser, bytes, idempotencyKey: key);
        using var b = await MediaScenario.UploadAsync(secondBrowser, bytes, idempotencyKey: key);

        Assert.Equal(HttpStatusCode.Accepted, b.StatusCode);
        Assert.NotEqual((string?)(await MediaScenario.ReadObjectAsync(a))["photoId"], (string?)(await MediaScenario.ReadObjectAsync(b))["photoId"]);
        Assert.Single(await _media.UploadsOfAsync(first.UserId));
        Assert.Single(await _media.UploadsOfAsync(second.UserId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chave com espaço")]
    [InlineData("../../quarantine")]
    [InlineData("á")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task AnUploadWithoutAValidIdempotencyKeyIsRefused(string? key)
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        var content = new ByteArrayContent(MediaScenario.JpegLike());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        using var response = await MediaScenario.UploadContentAsync(browser, content, key);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var errors = problem["errors"]!.AsObject();
        Assert.Equal(["Idempotency-Key"], errors.Select(pair => pair.Key));
        Assert.Equal("Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'.", Assert.Single(errors["Idempotency-Key"]!.AsArray())!.GetValue<string>());
        Assert.False(response.Headers.Contains("Duora-Validation-Format"));
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
        Assert.Equal(0, _media.StoredObjectCount());
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("image/gif")]
    [InlineData("application/octet-stream")]
    [InlineData("multipart/form-data; boundary=abc")]
    public async Task AContentTypeOutsideTheAllowlistIs415(string contentType)
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);

        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(), contentType);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE");
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("codes-v1")]
    public async Task OneByteOverFiveMebibytesIs413BeforeAnythingIsStored(string? selector)
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);

        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike((5 * 1024 * 1024) + 1), validationFormat: selector);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE");
        Assert.False(response.Headers.Contains("Duora-Validation-Format"));
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
        Assert.Equal(0, _media.StoredObjectCount());
    }

    [Fact]
    public async Task ExactlyFiveMebibytesIsAccepted()
    {
        using var browser = await SignInAsync(await NewAccountAsync());

        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(5 * 1024 * 1024));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("codes-v1")]
    public async Task AChunkedBodyWithoutContentLengthIs411(string? selector)
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        var content = new StreamContent(new ForwardOnlyStream(MediaScenario.JpegLike()));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        using var response = await MediaScenario.UploadContentAsync(browser, content, MediaScenario.NewKey(), selector);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.LengthRequired, "LENGTH_REQUIRED");
        Assert.False(response.Headers.Contains("Duora-Validation-Format"));
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
    }

    [Fact]
    public async Task AnEmptyBodyIsRefused()
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);

        using var response = await MediaScenario.UploadAsync(browser, []);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var errors = problem["errors"]!.AsObject();
        Assert.Equal(["body"], errors.Select(pair => pair.Key));
        Assert.Equal("A foto está vazia.", Assert.Single(errors["body"]!.AsArray())!.GetValue<string>());
        Assert.False(response.Headers.Contains("Duora-Validation-Format"));
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
        Assert.Equal(0, _media.StoredObjectCount());
    }

    [Fact]
    public async Task PendingUploadsPerAccountAreCapped()
    {
        // Seis envios simultâneos de chaves diferentes: o lock da linha do dono serializa a contagem, então só três
        // reservam vaga (teto padrão), sem write skew.
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        const int Attempts = 6;
        using var barrier = new Barrier(Attempts);

        var responses = await Task.WhenAll(Enumerable.Range(0, Attempts).Select(i => Task.Run(async () =>
        {
            barrier.SignalAndWait(TestContext.Current.CancellationToken);
            return await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(fill: (byte)i));
        })));

        try
        {
            Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
            var refused = responses.Where(r => r.StatusCode != HttpStatusCode.Accepted).ToList();
            Assert.Equal(3, refused.Count);
            foreach (var response in refused)
            {
                await ProblemAssert.IsProblemAsync(response, HttpStatusCode.TooManyRequests, "UPLOAD_PENDING_LIMIT");
                ProblemAssert.RetryAfterSeconds(response);
            }

            Assert.Equal(3, (await _media.UploadsOfAsync(account.UserId)).Count);
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
    public async Task AFinishedUploadFreesItsSlot()
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        for (var i = 0; i < 3; i++)
        {
            using var accepted = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(fill: (byte)i));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        var first = (await _media.UploadsOfAsync(account.UserId))[0];
        await _media.ExecuteAsync("update media.uploads set state = 'Rejected', rejection_code = 'CORRUPT_IMAGE' where id = $1", first.Id);

        using var next = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(fill: 9));

        Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
    }

    [Fact]
    public async Task AnAbandonedReservationNeitherHoldsASlotNorBlocksItsKey()
    {
        // Web reiniciado no meio do streaming deixa um Pending para trás: passado o prazo, ele não conta no teto e a
        // mesma chave pode ser reenviada.
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        var key = MediaScenario.NewKey();
        using (await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(), idempotencyKey: key))
        {
        }

        var stuck = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        await _media.ExecuteAsync(
            "update media.uploads set state = 'Pending', content_sha256 = null, created_at = created_at - interval '1 hour' where id = $1",
            stuck.Id);

        using var retried = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(), idempotencyKey: key);

        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        Assert.Equal("Quarantined", upload.State);
        Assert.NotEqual(stuck.Id, upload.Id);
    }

    [Fact]
    public async Task AnUploadStillStreamingWithTheSameKeyAsksTheClientToRetry()
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        var key = MediaScenario.NewKey();
        using (await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(), idempotencyKey: key))
        {
        }

        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        await _media.ExecuteAsync("update media.uploads set state = 'Pending', content_sha256 = null where id = $1", upload.Id);

        using var concurrent = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(), idempotencyKey: key);

        await ProblemAssert.IsProblemAsync(concurrent, HttpStatusCode.Conflict, "IDEMPOTENCY_REQUEST_IN_PROGRESS");
        ProblemAssert.RetryAfterSeconds(concurrent);
    }

    [Theory]
    [InlineData("Restricted", null)]
    [InlineData("Suspended", null)]
    [InlineData("DeletionPending", null)]
    [InlineData("Restricted", "codes-v1")]
    [InlineData("Suspended", "codes-v1")]
    [InlineData("DeletionPending", "codes-v1")]
    public async Task ARestrictedAccountCannotUpload(string status, string? selector)
    {
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        await _media.ExecuteAsync("update identity.users set status = $2 where id = $1", account.UserId, status);

        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(), validationFormat: selector);

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"Conta {status} recebeu {(int)response.StatusCode}.");
        Assert.False(response.Headers.Contains("Duora-Validation-Format"));
        Assert.Empty(await _media.UploadsOfAsync(account.UserId));
    }

    [Fact]
    public async Task MediaResponsesNeverContainStorageKeys()
    {
        // TM-42: a chave do bucket não sai em DTO, header nem log, nem no replay idempotente.
        var account = await NewAccountAsync();
        using var browser = await SignInAsync(account);
        var key = MediaScenario.NewKey();
        var bytes = MediaScenario.JpegLike();
        using var created = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);
        using var replayed = await MediaScenario.UploadAsync(browser, bytes, idempotencyKey: key);
        var upload = Assert.Single(await _media.UploadsOfAsync(account.UserId));
        using var status = await browser.GetAsync($"/api/v1/me/photos/{upload.Id}/status");
        var token = upload.QuarantineKey["quarantine/".Length..];

        foreach (var response in new[] { created, replayed, status })
        {
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers).Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
            Assert.DoesNotContain("quarantine/", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(token, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(token, headers, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(token, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void TheOriginalHasNoReadRoute()
    {
        // TM-44: the exact inventory admits metadata and guarded review derivatives, never original upload bytes.
        var mediaReads = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") ?? true)
            .Select(endpoint => "/" + (endpoint.RoutePattern.RawText ?? "").Trim('/'))
            .Where(path => path.Contains("photo", StringComparison.OrdinalIgnoreCase)
                || path.Contains("media", StringComparison.OrdinalIgnoreCase)
                || path.Contains("upload", StringComparison.OrdinalIgnoreCase));

        string[] expected =
        [
            "/api/v1/me/photos",
            "/api/v1/me/photos/{photoId:guid}/status",
            "/api/v1/me/photos/{photoId:guid}/review-history",
            "/api/v1/media/{assetId:guid}",
            "/api/v1/admin/photo-reviews/capabilities",
            "/api/v1/admin/photo-reviews",
            "/api/v1/admin/photo-reviews/{caseId:guid}",
            "/api/v1/admin/photo-reviews/{caseId:guid}/evidence/{variant}",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), mediaReads.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(mediaReads, path => path.Contains("original", StringComparison.OrdinalIgnoreCase)
            || path.Contains("quarantine", StringComparison.OrdinalIgnoreCase));
    }

    private Task<TestAccount> NewAccountAsync() => _media.Identity.CreateConfirmedAccountAsync();

    private Task<Browser> SignInAsync(TestAccount account) => _media.Identity.SignedInBrowserAsync(account);

    private static async Task<Guid> UploadAndReadIdAsync(Browser browser)
    {
        using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return Guid.Parse((string)(await MediaScenario.ReadObjectAsync(response))["photoId"]!);
    }
}
